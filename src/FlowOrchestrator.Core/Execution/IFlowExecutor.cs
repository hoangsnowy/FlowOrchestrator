using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Bootstraps a run: persists its trigger data and resolves the entry step.
/// Successor steps are resolved by <see cref="IFlowGraphPlanner"/> after each step completes.
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
}
