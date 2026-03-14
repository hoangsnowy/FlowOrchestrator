namespace FlowOrchestrator.Core.Execution;

public interface IStepResult
{
    string Key { get; set; }
    string Status { get; set; }
    object? Result { get; set; }
    string? FailedReason { get; set; }
    bool ReThrow { get; set; }
    TimeSpan? DelayNextStep { get; set; }
}
