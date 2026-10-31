using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace FlowOrchestrator.Core.Observability;

/// <summary>One-call OpenTelemetry wiring for FlowOrchestrator hosts.</summary>
public static class FlowOrchestratorOpenTelemetryExtensions
{
    private const string OtlpExporterAssembly = "OpenTelemetry.Exporter.OpenTelemetryProtocol";

    /// <summary>
    /// Wires FlowOrchestrator's traces, metrics and logs into the OpenTelemetry SDK and, when an OTLP
    /// endpoint is configured and the host does not export already, exports all three.
    /// </summary>
    /// <typeparam name="TBuilder">A host builder, e.g. <c>WebApplicationBuilder</c> or <c>HostApplicationBuilder</c>.</typeparam>
    /// <param name="builder">The host builder.</param>
    /// <param name="configure">Optional settings: service name, exporter switch, extra instrumentation.</param>
    /// <returns>The same <paramref name="builder"/>.</returns>
    /// <remarks>
    /// <para>Logs are exported with <c>IncludeScopes</c> on, so every log line written inside a run — the
    /// engine's and your step handlers' — carries the engine's scope attributes as searchable fields:
    /// <c>RunId</c>, <c>FlowId</c>, <c>FlowName</c>, <c>StepKey</c>, <c>StepType</c>, <c>TriggerKey</c>.
    /// Traces and logs share the trace / span id, so a log line links to its span.</para>
    /// <para>For operators, the only required setting is <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (plus
    /// <c>OTEL_EXPORTER_OTLP_HEADERS</c> / <c>OTEL_EXPORTER_OTLP_PROTOCOL</c> as the backend needs).</para>
    /// <para><b>Hosts that already export</b> (Aspire ServiceDefaults' <c>UseOtlpExporter()</c>, any
    /// <c>AddOtlpExporter()</c>, or their own <c>builder.Logging.AddOpenTelemetry(...)</c>): call this
    /// <i>after</i> that setup. The OTLP exporter is then left to the host — the OpenTelemetry SDK throws at
    /// startup when <c>UseOtlpExporter()</c> is combined with another OTLP registration — and this call only
    /// adds FlowOrchestrator's sources, meters and log scopes to the host's pipeline. Calling it more than once
    /// is a no-op after the first call.</para>
    /// </remarks>
    public static TBuilder AddFlowOrchestratorOpenTelemetry<TBuilder>(this TBuilder builder, Action<FlowOrchestratorOpenTelemetryOptions>? configure = null)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        if (services.Any(d => d.ServiceType == typeof(OpenTelemetryWiringMarker)))
            return builder;
        services.AddSingleton<OpenTelemetryWiringMarker>();

        var options = new FlowOrchestratorOpenTelemetryOptions();
        configure?.Invoke(options);

        // Decided before this call adds its own logging options, so only the host's setup counts.
        var useOtlp = ShouldUseOtlp(options, builder.Configuration, HostAlreadyExports(services));

        var serviceName = ResolveServiceName(options, builder.Configuration, builder.Environment);
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing.AddFlowOrchestratorInstrumentation();
                options.ConfigureTracing?.Invoke(tracing);
            })
            .WithMetrics(metrics =>
            {
                metrics.AddFlowOrchestratorInstrumentation();
                options.ConfigureMetrics?.Invoke(metrics);
            })
            .WithLogging(configureBuilder: null, configureOptions: logging =>
            {
                logging.IncludeScopes = true;
                logging.IncludeFormattedMessage = true;
                logging.ParseStateValues = true;
                options.ConfigureLogging?.Invoke(logging);
            });

        if (useOtlp)
            otel.UseOtlpExporter();

        return builder;
    }

    /// <summary>Explicit name, then <c>OTEL_SERVICE_NAME</c>, then the application name.</summary>
    internal static string ResolveServiceName(FlowOrchestratorOpenTelemetryOptions options, IConfiguration configuration, IHostEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(options.ServiceName))
            return options.ServiceName;
        var fromEnvironment = configuration["OTEL_SERVICE_NAME"];
        return string.IsNullOrWhiteSpace(fromEnvironment) ? environment.ApplicationName : fromEnvironment;
    }

    /// <summary>
    /// The explicit switch wins; otherwise on exactly when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set and the
    /// host does not already export.
    /// </summary>
    internal static bool ShouldUseOtlp(FlowOrchestratorOpenTelemetryOptions options, IConfiguration configuration, bool hostAlreadyExports)
        => options.UseOtlpExporter
           ?? (!hostAlreadyExports && !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]));

    /// <summary>
    /// <see langword="true"/> when the host registered the OTLP exporter (any signal, or <c>UseOtlpExporter</c>)
    /// or configured OpenTelemetry logging itself, whose options callback may add an exporter this call
    /// cannot see.
    /// </summary>
    internal static bool HostAlreadyExports(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(IConfigureOptions<OpenTelemetryLoggerOptions>)
                || IsOtlpType(descriptor.ServiceType)
                || IsOtlpType(descriptor.ImplementationType)
                || IsOtlpType(descriptor.ImplementationInstance?.GetType()))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsOtlpType(Type? type)
    {
        if (type is null)
            return false;
        if (string.Equals(type.Assembly.GetName().Name, OtlpExporterAssembly, StringComparison.Ordinal))
            return true;
        if (!type.IsGenericType)
            return false;
        foreach (var argument in type.GetGenericArguments())
        {
            if (IsOtlpType(argument))
                return true;
        }
        return false;
    }

    /// <summary>Registered once so a second call is a no-op.</summary>
    private sealed class OpenTelemetryWiringMarker;
}
