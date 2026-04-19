using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// The outcome of a step execution returned by <see cref="IStepHandler"/> or
/// <see cref="IStepHandler{TInput}"/>. Controls the orchestrator's next action.
/// </summary>
public interface IStepResult
{
    /// <summary>
    /// The key of the step that produced this result.
    /// Set automatically if not populated by the handler.
    /// </summary>
    string Key { get; set; }

    /// <summary>
    /// Terminal or intermediate status of the step.
    /// A <see cref="StepStatus.Pending"/> status with <see cref="DelayNextStep"/> instructs
    /// Hangfire to reschedule (polling pattern).
    /// </summary>
    StepStatus Status { get; set; }

    /// <summary>
    /// Optional output value serialised to JSON and stored in <c>IOutputsRepository</c>.
    /// Available to subsequent steps via <c>@outputs('stepKey')</c> expressions.
    /// </summary>
    object? Result { get; set; }

    /// <summary>
    /// Human-readable error message recorded when <see cref="Status"/> is <see cref="StepStatus.Failed"/>.
    /// </summary>
    string? FailedReason { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the orchestrator rethrows the step's exception after recording it,
    /// allowing Hangfire's built-in retry policy to kick in.
    /// </summary>
    bool ReThrow { get; set; }

    /// <summary>
    /// When non-<see langword="null"/>, the orchestrator schedules the next step invocation
    /// after this delay instead of enqueueing it immediately (used by <see cref="PollableStepHandler{TInput}"/>).
    /// </summary>
    TimeSpan? DelayNextStep { get; set; }
}
