using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Storage;

public interface IFlowRepository
{
    ValueTask<IReadOnlyList<IFlowDefinition>> GetAllFlowsAsync();
}
