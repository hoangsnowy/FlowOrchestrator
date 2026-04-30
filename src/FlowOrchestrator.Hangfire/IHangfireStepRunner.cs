using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using Hangfire.Server;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Executes individual flow steps as Hangfire background jobs.
/// Implemented by <see cref="HangfireFlowOrchestrator"/>.
/// </summary>
public interface IHangfireStepRunner
{
    /// <summary>
    /// Executes the given step, persists its output, and enqueues any subsequent ready steps.
    /// Called by Hangfire when the step's background job fires.
    /// </summary>
    /// <param name="ctx">The ambient execution context carrying RunId and trigger data.</param>
    /// <param name="flowId">The identifier of the flow that owns this step. The flow definition is rehydrated server-side via <see cref="IFlowRepository"/> to avoid serialising the full manifest through Hangfire's argument store.</param>
    /// <param name="step">The step instance with resolved inputs.</param>
    /// <param name="performContext">Hangfire job context; <see langword="null"/> when called outside a Hangfire job.</param>
    ValueTask<object?> RunStepAsync(IExecutionContext ctx, Guid flowId, IStepInstance step, PerformContext? performContext = null);

    /// <summary>
    /// Back-compat overload for jobs enqueued before the dispatcher was refactored to pass
    /// only <c>flow.Id</c>. Hangfire stores invocations by exact method signature, so this
    /// method must keep its original shape for old retries to resolve. Internally it
    /// extracts <see cref="IFlowDefinition.Id"/> and delegates to the new overload.
    /// </summary>
    [Obsolete("Use the Guid flowId overload. This method exists only so Hangfire can resolve job payloads enqueued before Plan 05.")]
    ValueTask<object?> RunStepAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, PerformContext? performContext = null);

    /// <summary>
    /// Resets a step's state and re-executes it from scratch. Used by the dashboard Retry button.
    /// Restores trigger data from <c>IOutputsRepository</c> before re-running.
    /// </summary>
    /// <param name="flowId">The flow that owns the run.</param>
    /// <param name="runId">The run containing the step to retry.</param>
    /// <param name="stepKey">The key of the step to retry.</param>
    /// <param name="performContext">Hangfire job context.</param>
    ValueTask<object?> RetryStepAsync(Guid flowId, Guid runId, string stepKey, PerformContext? performContext = null);
}
