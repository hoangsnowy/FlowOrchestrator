using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Drives the high-level flow execution lifecycle: bootstrapping the first step on trigger
/// and resolving the next step after each step completes.
/// </summary>
public interface IFlowExecutor
{
    /// <summary>
    /// Persists trigger data, then builds and returns the first <see cref="IStepInstance"/>
    /// to be enqueued as a Hangfire job.
    /// </summary>
    /// <param name="context">The trigger context carrying the flow, trigger event, and run metadata.</param>
    /// <returns>The entry step instance ready for enqueueing.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the flow manifest contains no step with an empty <c>runAfter</c>.
    /// </exception>
    ValueTask<IStepInstance> TriggerFlow(ITriggerContext context);

    /// <summary>
    /// Evaluates the flow graph after <paramref name="currentStep"/> completes and returns
    /// the next <see cref="IStepInstance"/> to enqueue, or <see langword="null"/> if the run is finished.
    /// </summary>
    /// <param name="context">The ambient execution context for this run.</param>
    /// <param name="flow">The flow definition being executed.</param>
    /// <param name="currentStep">The step that just finished.</param>
    /// <param name="result">The result of the completed step, used to check <c>runAfter</c> conditions.</param>
    ValueTask<IStepInstance?> GetNextStep(IExecutionContext context, IFlowDefinition flow, IStepInstance currentStep, IStepResult result);
}
