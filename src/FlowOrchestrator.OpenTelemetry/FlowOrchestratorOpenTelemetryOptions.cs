using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace FlowOrchestrator.Core.Observability;

/// <summary>Options for <see cref="FlowOrchestratorOpenTelemetryExtensions.AddFlowOrchestratorOpenTelemetry{TBuilder}"/>.</summary>
public sealed class FlowOrchestratorOpenTelemetryOptions
{
    /// <summary>
    /// <c>service.name</c> resource attribute. Defaults to the <c>OTEL_SERVICE_NAME</c> setting, then to the
    /// host's application name.
    /// </summary>
    public string? ServiceName { get; set; }

    /// <summary>
    /// Exports all three signals over OTLP. <see langword="null"/> (the default) turns the exporter on when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is configured, so a host without a collector does not log export
    /// failures; the standard <c>OTEL_EXPORTER_OTLP_*</c> settings configure it.
    /// </summary>
    public bool? UseOtlpExporter { get; set; }

    /// <summary>Adds further tracing sources / instrumentation (e.g. ASP.NET Core, HttpClient).</summary>
    public Action<TracerProviderBuilder>? ConfigureTracing { get; set; }

    /// <summary>Adds further meters / instrumentation.</summary>
    public Action<MeterProviderBuilder>? ConfigureMetrics { get; set; }

    /// <summary>
    /// Adjusts the log exporter options after the defaults (<c>IncludeScopes</c>, <c>IncludeFormattedMessage</c>,
    /// <c>ParseStateValues</c> all on) are applied.
    /// </summary>
    public Action<OpenTelemetryLoggerOptions>? ConfigureLogging { get; set; }
}
