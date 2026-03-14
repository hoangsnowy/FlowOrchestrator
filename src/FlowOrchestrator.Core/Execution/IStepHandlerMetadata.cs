using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

public interface IStepHandlerMetadata
{
    string Type { get; }

    ValueTask<IStepResult> ExecuteAsync(IServiceProvider sp, IExecutionContext ctx, IFlowDefinition flow, IStepInstance step);
}
