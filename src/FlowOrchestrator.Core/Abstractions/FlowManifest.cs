namespace FlowOrchestrator.Core.Abstractions;

public sealed class FlowManifest
{
    public FlowTriggerCollection Triggers { get; set; } = new();
    public StepCollection Steps { get; set; } = new();
}
