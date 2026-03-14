namespace FlowOrchestrator.Core.Abstractions;

public sealed class LoopStepMetadata : StepMetadata, IScopedStep
{
    /// <summary>
    /// Expression string or array value.
    /// </summary>
    public object? ForEach { get; set; }

    public int ConcurrencyLimit { get; set; } = 1;

    public StepCollection Steps { get; set; } = new();
}
