using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

public interface IFlowExecutor
{
    ValueTask<IStepInstance> TriggerFlow(ITriggerContext context);
    ValueTask<IStepInstance?> GetNextStep(IExecutionContext context, IFlowDefinition flow, IStepInstance currentStep, IStepResult result);
}
