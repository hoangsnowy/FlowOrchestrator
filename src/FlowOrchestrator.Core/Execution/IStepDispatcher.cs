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

    /// <summary>
    /// Whether a step this dispatcher has accepted (enqueued or scheduled) is LOST when the process that
    /// accepted it stops — <see langword="true"/> for an in-process queue, <see langword="false"/> for a
    /// runtime backed by durable storage (Hangfire job storage, a Service Bus queue).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Startup recovery reads this to interpret the dispatch ledger. Every dispatched step carries a ledger
    /// row in the run store; on a durable runtime a ledger row means "the runtime still holds this step and
    /// will deliver it", so recovery leaves it alone. On a non-durable runtime the queued work died with the
    /// process while the row survived (the run store can be durable even when the runtime is not, e.g.
    /// <c>UseInMemoryRuntime()</c> with <c>UseSqlServer()</c>), so recovery re-enqueues every ledgered step
    /// that has not started — including a <c>Pending</c> poll whose delayed re-attempt was lost — without
    /// recording a second ledger row. The execution claim still guarantees a step runs once.
    /// </para>
    /// <para>
    /// Defaults to <see langword="false"/> (durable), so existing runtimes keep their behaviour. The polarity
    /// is deliberate: a mocking library that returns <c>default(bool)</c> for an un-configured member
    /// reproduces the durable behaviour every pre-2.0 test was written against. A decorator around another
    /// dispatcher must forward this property.
    /// </para>
    /// </remarks>
    bool LosesQueuedStepsOnRestart => false;
}
