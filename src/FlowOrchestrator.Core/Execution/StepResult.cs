namespace FlowOrchestrator.Core.Execution;

public sealed class StepResult : IStepResult
{
    public string Key { get; set; } = default!;
    public string Status { get; set; } = "Succeeded";
    public object? Result { get; set; }
    public string? FailedReason { get; set; }
    public bool ReThrow { get; set; }
    public TimeSpan? DelayNextStep { get; set; }
}
