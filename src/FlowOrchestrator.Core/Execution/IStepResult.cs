using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

public interface IStepResult
{
    string Key { get; set; }
    StepStatus Status { get; set; }
    object? Result { get; set; }
    string? FailedReason { get; set; }
    bool ReThrow { get; set; }
    TimeSpan? DelayNextStep { get; set; }
}
