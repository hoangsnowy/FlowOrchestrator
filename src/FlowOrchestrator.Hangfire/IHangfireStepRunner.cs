using FlowOrchestrator.Core.Execution;
using Hangfire.Server;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Executes individual flow steps as Hangfire background jobs.
/// Implemented by <see cref="HangfireFlowOrchestrator"/>.
/// </summary>
public interface IHangfireStepRunner
{
    /// <summary>
    /// Executes the step described by <paramref name="envelope"/>, persists its output, and enqueues
    /// any subsequent ready steps. Called by Hangfire when the step's background job fires.
    /// </summary>
    /// <param name="envelope">
    /// Ids, inputs and scheduling data of the step. The flow definition is resolved from
    /// <c>FlowRegistry</c> and the trigger payload from the run's stored trigger data, so neither
    /// travels through Hangfire's argument store.
    /// </param>
    /// <param name="performContext">Hangfire job context; <see langword="null"/> when called outside a Hangfire job.</param>
    ValueTask<object?> RunStepAsync(HangfireStepEnvelope envelope, PerformContext? performContext = null);

    /// <summary>
    /// Back-compat entry point for step jobs enqueued by FlowOrchestrator 1.17 – 1.x, which passed the
    /// full execution context and step instance as job arguments. Hangfire resolves a stored job by its
    /// exact method signature, so this overload keeps those in-flight jobs runnable across the upgrade.
    /// New dispatches always use <see cref="RunStepAsync(HangfireStepEnvelope, PerformContext?)"/>.
    /// </summary>
    /// <param name="ctx">The execution context deserialised from the legacy job payload.</param>
    /// <param name="flowId">The flow that owns the step.</param>
    /// <param name="step">The step instance deserialised from the legacy job payload.</param>
    /// <param name="performContext">Hangfire job context; <see langword="null"/> when called outside a Hangfire job.</param>
    [Obsolete("Only resolves step jobs enqueued before 2.0. New jobs use RunStepAsync(HangfireStepEnvelope, PerformContext?). Drain those queues before the next major release removes this overload.")]
    ValueTask<object?> RunStepAsync(IExecutionContext ctx, Guid flowId, IStepInstance step, PerformContext? performContext = null);

    /// <summary>
    /// Resets a <c>Failed</c> step and dispatches a fresh attempt of it; see
    /// <see cref="IFlowOrchestrator.RetryStepAsync"/>. The attempt runs as its own job, not inside this one,
    /// and the returned value is a <see cref="StepRetryResult"/>.
    /// </summary>
    /// <param name="flowId">The flow that owns the run.</param>
    /// <param name="runId">The run containing the step to retry.</param>
    /// <param name="stepKey">The key of the step to retry.</param>
    /// <param name="performContext">Hangfire job context.</param>
    ValueTask<object?> RetryStepAsync(Guid flowId, Guid runId, string stepKey, PerformContext? performContext = null);
}
