using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Bridges the core engine to the runtime that physically invokes
/// <see cref="IFlowOrchestrator.RunStepAsync"/> — Hangfire today, a queue consumer or
/// in-memory channel tomorrow — without the engine knowing which one is active.
/// </summary>
/// <remarks>
/// Implementations must guarantee that all arguments are serialisable by the backing
/// runtime so that a different worker process can deserialise and re-enter the engine.
/// </remarks>
public interface IStepDispatcher
{
    /// <summary>
    /// Enqueues immediate execution of a step.
    /// </summary>
    /// <param name="context">The current execution context carrying RunId, trigger data, and principal.</param>
    /// <param name="flow">The flow definition the step belongs to.</param>
    /// <param name="step">The step instance to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An opaque runtime-specific job or message identifier, or <see langword="null"/> if the runtime does not produce one.</returns>
    ValueTask<string?> EnqueueStepAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance step,
        CancellationToken ct = default);

    /// <summary>
    /// Schedules deferred execution of a step after <paramref name="delay"/> has elapsed.
    /// Used for polling steps (<see cref="StepStatus.Pending"/>), backoff retries, and <c>runAfter</c>-with-delay scenarios.
    /// </summary>
    /// <param name="context">The current execution context.</param>
    /// <param name="flow">The flow definition the step belongs to.</param>
    /// <param name="step">The step instance to execute.</param>
    /// <param name="delay">How long to wait before the step is eligible to run.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An opaque runtime-specific job or message identifier.</returns>
    ValueTask<string?> ScheduleStepAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance step,
        TimeSpan delay,
        CancellationToken ct = default);
}
