using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Storage;

public sealed class InMemoryFlowRepository : IFlowRepository
{
    private readonly List<IFlowDefinition> _flows = new();

    public void Add(IFlowDefinition definition) => _flows.Add(definition);

    public ValueTask<IReadOnlyList<IFlowDefinition>> GetAllFlowsAsync()
        => ValueTask.FromResult<IReadOnlyList<IFlowDefinition>>(_flows);
}
