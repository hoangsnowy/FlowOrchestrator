using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Orchestrates the full lifecycle of a single step execution: resolving the handler,
/// evaluating input expressions, invoking the handler, and persisting the output.
/// Implemented by <c>DefaultStepExecutor</c> in the Hangfire layer.
/// </summary>
public interface IStepExecutor
{
    /// <summary>
    /// Executes the step described by <paramref name="step"/> within the given flow and context,
    /// returning the normalised result.
    /// </summary>
    /// <param name="context">The ambient execution context carrying RunId and trigger data.</param>
    /// <param name="flow">The flow definition that owns this step.</param>
    /// <param name="step">The resolved step instance with evaluated inputs.</param>
    ValueTask<IStepResult> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step);
}
