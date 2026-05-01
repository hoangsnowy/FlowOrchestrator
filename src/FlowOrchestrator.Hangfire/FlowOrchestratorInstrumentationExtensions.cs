using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using CoreObservability = FlowOrchestrator.Core.Observability;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Backwards-compatibility forwarders for the OpenTelemetry registration helpers that moved to
/// <see cref="CoreObservability.FlowOrchestratorInstrumentationExtensions"/> in v1.19.
/// </summary>
/// <remarks>
/// Existing user code calling <c>using FlowOrchestrator.Hangfire;</c> followed by
/// <c>tracer.AddFlowOrchestratorInstrumentation()</c> continues to compile, but the call site
/// produces a CS0618 obsolete-warning pointing at the new location. Remove these forwarders in
/// the next major release.
/// </remarks>
[Obsolete(
    "Use FlowOrchestrator.Core.Observability.FlowOrchestratorInstrumentationExtensions instead. " +
    "OTel registration is no longer Hangfire-specific.",
    error: false)]
public static class FlowOrchestratorInstrumentationExtensions
{
    /// <summary>Forwards to <see cref="CoreObservability.FlowOrchestratorInstrumentationExtensions.AddFlowOrchestratorInstrumentation(TracerProviderBuilder)"/>.</summary>
    [Obsolete(
        "Use FlowOrchestrator.Core.Observability.FlowOrchestratorInstrumentationExtensions.AddFlowOrchestratorInstrumentation instead.",
        error: false)]
    public static TracerProviderBuilder AddFlowOrchestratorInstrumentation(
        this TracerProviderBuilder builder)
        => CoreObservability.FlowOrchestratorInstrumentationExtensions.AddFlowOrchestratorInstrumentation(builder);

    /// <summary>Forwards to <see cref="CoreObservability.FlowOrchestratorInstrumentationExtensions.AddFlowOrchestratorInstrumentation(MeterProviderBuilder)"/>.</summary>
    [Obsolete(
        "Use FlowOrchestrator.Core.Observability.FlowOrchestratorInstrumentationExtensions.AddFlowOrchestratorInstrumentation instead.",
        error: false)]
    public static MeterProviderBuilder AddFlowOrchestratorInstrumentation(
        this MeterProviderBuilder builder)
        => CoreObservability.FlowOrchestratorInstrumentationExtensions.AddFlowOrchestratorInstrumentation(builder);
}
