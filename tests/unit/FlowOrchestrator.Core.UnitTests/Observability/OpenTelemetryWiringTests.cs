using System.Diagnostics;
using FlowOrchestrator.Core.Observability;
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
    [InlineData(null, null, false, false)]
    [InlineData(null, "http://otel-collector:4317", false, true)]
    [InlineData(null, "http://otel-collector:4317", true, false)]
    [InlineData(false, "http://otel-collector:4317", false, false)]
    [InlineData(true, null, true, true)]
    public void Otlp_exporter_follows_the_endpoint_unless_the_host_exports_or_it_is_overridden(bool? explicitSwitch, string? endpoint, bool hostExports, bool expected)
    {
        // Arrange — no collector configured must not mean a stream of export failures, and a host that
        // already exports keeps its own exporter.
        var builder = endpoint is null ? Builder() : Builder(("OTEL_EXPORTER_OTLP_ENDPOINT", endpoint));
        var options = new FlowOrchestratorOpenTelemetryOptions { UseOtlpExporter = explicitSwitch };

        // Act
        var result = FlowOrchestratorOpenTelemetryExtensions.ShouldUseOtlp(options, builder.Configuration, hostExports);

        // Assert
        Assert.Equal(expected, result);
    }

    public static TheoryData<string> HostSetups => ["aspire-use-otlp", "tracing-add-otlp", "logging-options-add-otlp", "none", "called-twice"];

    [Theory]
    [MemberData(nameof(HostSetups))]
    public async Task Coexists_with_a_host_that_already_exports_over_otlp(string hostSetup)
    {
        // Arrange — the OpenTelemetry SDK throws at provider build time when UseOtlpExporter() meets another
        // OTLP registration; every host shape below must still start.
        // Short exporter timeout: nothing listens on the endpoint, and the shutdown flush must not stall the suite.
        var builder = Builder(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:1"), ("OTEL_EXPORTER_OTLP_TIMEOUT", "50"));
        switch (hostSetup)
        {
            case "aspire-use-otlp": builder.Services.AddOpenTelemetry().UseOtlpExporter(); break;
            case "tracing-add-otlp": builder.Services.AddOpenTelemetry().WithTracing(t => t.AddOtlpExporter()); break;
            case "logging-options-add-otlp": builder.Logging.AddOpenTelemetry(o => o.AddOtlpExporter()); break;
            case "called-twice": builder.AddFlowOrchestratorOpenTelemetry(); break;
        }
        builder.AddFlowOrchestratorOpenTelemetry();

        // Act
        var ex = await Record.ExceptionAsync(async () =>
        {
            using var host = builder.Build();
            host.Services.GetService<TracerProvider>();
            host.Services.GetService<OpenTelemetry.Metrics.MeterProvider>();
            host.Services.GetService<LoggerProvider>();
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("probe").LogInformation("hello");
            await Task.CompletedTask;
        });

        // Assert
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("aspire-use-otlp", true)]
    [InlineData("tracing-add-otlp", true)]
    [InlineData("logging-options", true)]
    public void Detects_a_host_export_pipeline(string hostSetup, bool expected)
    {
        // Arrange
        var builder = Builder();
        switch (hostSetup)
        {
            case "aspire-use-otlp": builder.Services.AddOpenTelemetry().UseOtlpExporter(); break;
            case "tracing-add-otlp": builder.Services.AddOpenTelemetry().WithTracing(t => t.AddOtlpExporter()); break;
            case "logging-options": builder.Logging.AddOpenTelemetry(o => o.IncludeScopes = true); break;
        }

        // Act
        var result = FlowOrchestratorOpenTelemetryExtensions.HostAlreadyExports(builder.Services);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task One_call_exports_logs_with_scopes_and_flow_orchestrator_spans()
    {
        // Arrange — the "FlowOrchestrator" ActivitySource is process-wide, so engine tests running in parallel
        // emit spans into this provider too. Only the span tagged with this test's probe id counts.
        var spans = new List<Activity>();
        var probe = Guid.NewGuid().ToString("N");
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
            activity?.SetTag("test.probe", probe);
        }
        // Deterministic stand-in for an engine test running in parallel (what failed the v1.34.0 tag
        // build): an untagged span from the same source lands in this provider.
        using (FlowOrchestratorTelemetry.SharedActivitySource.StartActivity("flow.trigger"))
        {
        }
        await host.StopAsync();

        // Assert
        Assert.True(logOptions.IncludeScopes);
        Assert.True(logOptions.IncludeFormattedMessage);
        Assert.True(logOptions.ParseStateValues);
        Activity[] mine;
        lock (spans) mine = spans.Where(s => Equals(s.GetTagItem("test.probe"), probe)).ToArray();
        var span = Assert.Single(mine);
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
