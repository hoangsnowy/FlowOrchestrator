using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Runtime-neutral entry point for the FlowOrchestrator execution engine.
/// Implemented by <see cref="FlowOrchestratorEngine"/>; consumed by runtime adapters
/// (Hangfire, in-memory pump, queue consumer) that handle the actual job dispatch.
/// </summary>
public interface IFlowOrchestrator
{
    /// <summary>
    /// Starts a new flow run: persists trigger data, registers idempotency key if present,
    /// creates the run record, and dispatches entry steps.
    /// </summary>
    /// <param name="context">
    /// Trigger context populated by the caller. <see cref="IExecutionContext.JobId"/> should be set
    /// to the runtime job or message ID before calling, if available.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An anonymous object with <c>runId</c> (Guid) and <c>duplicate</c> (bool).</returns>
    ValueTask<object?> TriggerAsync(ITriggerContext context, CancellationToken ct = default);

    /// <summary>
    /// Resolves the flow by <paramref name="flowId"/> and starts a run via its cron trigger.
    /// Used by recurring job runtimes (Hangfire recurring jobs, cron daemon, etc.).
    /// </summary>
    /// <param name="flowId">The flow to trigger.</param>
    /// <param name="triggerKey">The manifest trigger key (e.g. <c>"schedule"</c>).</param>
    /// <param name="jobId">Optional runtime job/message ID to correlate with the run record.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<object?> TriggerByScheduleAsync(Guid flowId, string triggerKey, string? jobId = null, CancellationToken ct = default);

    /// <summary>
    /// Executes one step: runs the handler, persists the result, and dispatches the next ready steps.
    /// Called by the runtime adapter when a dispatched step job fires.
    /// </summary>
    /// <param name="context">The ambient execution context carrying RunId and trigger data.</param>
    /// <param name="flow">The flow definition that owns this step.</param>
    /// <param name="step">The step instance with resolved inputs.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<object?> RunStepAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step, CancellationToken ct = default);

    /// <summary>
    /// Resets a <c>Failed</c> step and dispatches a fresh attempt of it through the runtime. Used by the
    /// dashboard Retry button.
    /// </summary>
    /// <param name="flowId">The flow that owns the run.</param>
    /// <param name="runId">The run containing the step to retry.</param>
    /// <param name="stepKey">The key of the step to retry.</param>
    /// <param name="ct">
    /// Cancels the request before the step is reset; once the reset is durable it is ignored, so an abandoned
    /// caller cannot leave the step reset but never dispatched.
    /// </param>
    /// <returns>
    /// A <see cref="StepRetryResult"/>: <see cref="StepRetryOutcome.Accepted"/> when the step was reset and
    /// dispatched, <see cref="StepRetryOutcome.NotFailed"/> when it is not <c>Failed</c>, and
    /// <see cref="StepRetryOutcome.Conflict"/> when a concurrent retry reset it first.
    /// </returns>
    /// <remarks>
    /// Since 2.0 the step runs asynchronously on the runtime, not on the caller's thread, and only a
    /// <c>Failed</c> step can be retried. The reset is a compare-and-swap, so of two concurrent retries
    /// exactly one is accepted.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The flow is not registered, or the step is not in its manifest.</exception>
    ValueTask<object?> RetryStepAsync(Guid flowId, Guid runId, string stepKey, CancellationToken ct = default);
}
