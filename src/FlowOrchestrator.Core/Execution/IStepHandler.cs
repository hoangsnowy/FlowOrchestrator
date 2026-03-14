using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

public interface IStepHandler
{
    ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step);
}

public interface IStepHandler<TInput>
{
    ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance<TInput> step);
}
