using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Extension methods for wiring <see cref="FlowOrchestratorTelemetry"/> into an OpenTelemetry pipeline.
/// </summary>
public static class FlowOrchestratorInstrumentationExtensions
{
    /// <summary>
    /// Registers the FlowOrchestrator <see cref="System.Diagnostics.ActivitySource"/> so that
    /// flow and step execution spans are captured by the tracing pipeline.
    /// </summary>
    /// <param name="builder">The <see cref="TracerProviderBuilder"/> to configure.</param>
    /// <returns>The same builder for chaining.</returns>
    public static TracerProviderBuilder AddFlowOrchestratorInstrumentation(
        this TracerProviderBuilder builder)
    {
        return builder.AddSource(FlowOrchestratorTelemetry.SourceName);
    }

    /// <summary>
    /// Registers the FlowOrchestrator <see cref="System.Diagnostics.Metrics.Meter"/> so that
    /// run/step counters and duration histograms are captured by the metrics pipeline.
    /// </summary>
    /// <param name="builder">The <see cref="MeterProviderBuilder"/> to configure.</param>
    /// <returns>The same builder for chaining.</returns>
    public static MeterProviderBuilder AddFlowOrchestratorInstrumentation(
        this MeterProviderBuilder builder)
    {
        return builder.AddMeter(FlowOrchestratorTelemetry.SourceName);
    }
}
