using System.Diagnostics;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace FlowOrchestrator.Core.Tests.Observability;

/// <summary>
/// Pins the one-call OpenTelemetry wiring (<c>AddFlowOrchestratorOpenTelemetry</c>) and the human-readable
/// scope attributes the engine adds to every log line inside a run.
/// </summary>
public sealed class OpenTelemetryWiringTests
{
    private static HostApplicationBuilder Builder(params (string Key, string Value)[] settings)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ApplicationName = "orders-worker", DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));
        return builder;
    }

    [Fact]
    public void Service_name_prefers_explicit_then_otel_setting_then_application_name()
    {
        // Arrange
        var explicitName = new FlowOrchestratorOpenTelemetryOptions { ServiceName = "explicit" };
        var none = new FlowOrchestratorOpenTelemetryOptions();
        var withEnv = Builder(("OTEL_SERVICE_NAME", "from-env"));
        var withoutEnv = Builder();

        // Act
        var a = FlowOrchestratorOpenTelemetryExtensions.ResolveServiceName(explicitName, withEnv.Configuration, withEnv.Environment);
        var b = FlowOrchestratorOpenTelemetryExtensions.ResolveServiceName(none, withEnv.Configuration, withEnv.Environment);
        var c = FlowOrchestratorOpenTelemetryExtensions.ResolveServiceName(none, withoutEnv.Configuration, withoutEnv.Environment);

        // Assert
        Assert.Equal("explicit", a);
        Assert.Equal("from-env", b);
        Assert.Equal("orders-worker", c);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, "http://otel-collector:4317", true)]
    [InlineData(false, "http://otel-collector:4317", false)]
    [InlineData(true, null, true)]
    public void Otlp_exporter_follows_the_endpoint_setting_unless_overridden(bool? explicitSwitch, string? endpoint, bool expected)
    {
        // Arrange — no collector configured must not mean a stream of export failures.
        var builder = endpoint is null ? Builder() : Builder(("OTEL_EXPORTER_OTLP_ENDPOINT", endpoint));
        var options = new FlowOrchestratorOpenTelemetryOptions { UseOtlpExporter = explicitSwitch };

        // Act
        var result = FlowOrchestratorOpenTelemetryExtensions.ShouldUseOtlp(options, builder.Configuration);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task One_call_exports_logs_with_scopes_and_flow_orchestrator_spans()
    {
        // Arrange
        var spans = new List<Activity>();
        var builder = Builder();
        builder.AddFlowOrchestratorOpenTelemetry(o => o.ConfigureTracing = t => t.AddProcessor(new CaptureProcessor(spans)));
        using var host = builder.Build();
        await host.StartAsync();

        // Act
        var logOptions = host.Services.GetRequiredService<IOptionsMonitor<OpenTelemetryLoggerOptions>>().CurrentValue;
        host.Services.GetRequiredService<TracerProvider>();
        using (var activity = FlowOrchestratorTelemetry.SharedActivitySource.StartActivity("flow.step"))
        {
            activity?.SetTag("flow.name", "OrderFlow");
        }
        await host.StopAsync();

        // Assert
        Assert.True(logOptions.IncludeScopes);
        Assert.True(logOptions.IncludeFormattedMessage);
        Assert.True(logOptions.ParseStateValues);
        var span = Assert.Single(spans);
        Assert.Equal("OrderFlow", span.GetTagItem("flow.name"));
    }

    [Fact]
    public void Engine_scope_carries_readable_names_without_line_breaks()
    {
        // Arrange
        var logger = new ScopeCapturingLogger();
        var runId = Guid.NewGuid();
        var flowId = Guid.NewGuid();

        // Act
        using (EngineLogScope.Begin(logger, runId, flowId, "charge\r\ncard", attempt: 2, flowName: "OrderFlow", stepType: "Http\nCall", triggerKey: "webhook"))
        {
        }

        // Assert
        var state = Assert.Single(logger.Scopes);
        Assert.Equal(runId, state["RunId"]);
        Assert.Equal(flowId, state["FlowId"]);
        Assert.Equal("charge__card", state["StepKey"]);
        Assert.Equal(2, state["Attempt"]);
        Assert.Equal("OrderFlow", state["FlowName"]);
        Assert.Equal("Http_Call", state["StepType"]);
        Assert.Equal("webhook", state["TriggerKey"]);
    }

    [Fact]
    public void Engine_scope_legacy_overload_omits_names()
    {
        // Arrange
        var logger = new ScopeCapturingLogger();

        // Act
        using (EngineLogScope.Begin(logger, Guid.NewGuid(), Guid.NewGuid(), "s1"))
        {
        }

        // Assert
        var state = Assert.Single(logger.Scopes);
        Assert.False(state.ContainsKey("FlowName"));
        Assert.False(state.ContainsKey("StepType"));
        Assert.Equal("s1", state["StepKey"]);
    }

    private sealed class CaptureProcessor(List<Activity> spans) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            lock (spans) spans.Add(data);
        }
    }

    private sealed class ScopeCapturingLogger : ILogger
    {
        public List<IReadOnlyDictionary<string, object?>> Scopes { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IReadOnlyDictionary<string, object?> dictionary)
                Scopes.Add(dictionary);
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }
}
