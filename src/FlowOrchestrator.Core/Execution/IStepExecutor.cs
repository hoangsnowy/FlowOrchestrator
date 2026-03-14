using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

public interface IStepExecutor
{
    ValueTask<IStepResult> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step);
}
