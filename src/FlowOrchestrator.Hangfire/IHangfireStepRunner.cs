using FlowOrchestrator.Core.Abstractions;
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
    /// Executes the given step, persists its output, and enqueues any subsequent ready steps.
    /// Called by Hangfire when the step's background job fires.
    /// </summary>
    /// <param name="ctx">The ambient execution context carrying RunId and trigger data.</param>
    /// <param name="flow">The flow definition that owns this step.</param>
    /// <param name="step">The step instance with resolved inputs.</param>
    /// <param name="performContext">Hangfire job context; <see langword="null"/> when called outside a Hangfire job.</param>
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
