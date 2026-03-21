using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Core.Storage;

public interface IOutputsRepository
{
    ValueTask SaveStepOutputAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, IStepResult result);
    ValueTask SaveTriggerDataAsync(ITriggerContext ctx, IFlowDefinition flow, ITrigger trigger);
    ValueTask<object?> GetTriggerDataAsync(Guid runId);
    ValueTask SaveTriggerHeadersAsync(ITriggerContext ctx, IFlowDefinition flow, ITrigger trigger);
    ValueTask<IReadOnlyDictionary<string, string>?> GetTriggerHeadersAsync(Guid runId);
    ValueTask SaveStepInputAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step);
    ValueTask<object?> GetStepOutputAsync(Guid runId, string stepKey);
    ValueTask EndScopeAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step);
    ValueTask RecordEventAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, FlowEvent evt);
}
