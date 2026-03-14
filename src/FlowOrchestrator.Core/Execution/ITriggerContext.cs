using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

public interface ITriggerContext : IExecutionContext
{
    IFlowDefinition Flow { get; set; }
    ITrigger Trigger { get; set; }
    string? JobId { get; set; }
}
