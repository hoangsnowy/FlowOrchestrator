namespace FlowOrchestrator.Core.Abstractions;

/// <summary>
/// Root container for a flow's declarative definition, holding its triggers and steps.
/// Deserialized from JSON or constructed in code via <see cref="IFlowDefinition"/> implementations.
/// </summary>
public sealed class FlowManifest
{
    /// <summary>Named triggers that can start this flow (manual, webhook, or cron).</summary>
    public FlowTriggerCollection Triggers { get; set; } = new();

    /// <summary>
    /// Ordered step definitions keyed by step name.
    /// Execution order is determined by <see cref="StepMetadata.RunAfter"/> dependencies, not insertion order.
    /// </summary>
    public StepCollection Steps { get; set; } = new();
}
