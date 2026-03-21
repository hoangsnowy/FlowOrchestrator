using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Serialization;

namespace FlowOrchestrator.Core.Execution;

public sealed class StepResult : IStepResult
{
    public string Key { get; set; } = default!;
    public StepStatus Status { get; set; } = StepStatus.Succeeded;
    public object? Result { get; set; }
    public string? FailedReason { get; set; }
    public bool ReThrow { get; set; }
    public TimeSpan? DelayNextStep { get; set; }
}

public sealed class StepResult<T> : IStepResult
{
    public string Key { get; set; } = default!;
    public StepStatus Status { get; set; } = StepStatus.Succeeded;
    public T? Value { get; set; }
    public string? FailedReason { get; set; }
    public bool ReThrow { get; set; }
    public TimeSpan? DelayNextStep { get; set; }

    public object? Result
    {
        get => Value;
        set => Value = JsonValueConversion.Deserialize<T>(value);
    }
}
