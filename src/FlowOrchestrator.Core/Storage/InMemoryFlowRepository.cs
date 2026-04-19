using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// In-memory implementation of <see cref="IFlowRepository"/> that holds code-defined
/// <see cref="IFlowDefinition"/> instances registered via <c>AddFlow&lt;T&gt;()</c>.
/// </summary>
/// <remarks>
/// This repository is populated once at startup by <c>FlowSyncHostedService</c> and is
/// read-only afterwards. It stores the original definition objects, not serialized records.
/// </remarks>
public sealed class InMemoryFlowRepository : IFlowRepository
{
    private readonly List<IFlowDefinition> _flows = new();

    /// <summary>Adds a flow definition to the in-memory collection.</summary>
    /// <param name="definition">The flow definition instance to register.</param>
    public void Add(IFlowDefinition definition) => _flows.Add(definition);

    /// <summary>Returns all registered flow definitions.</summary>
    public ValueTask<IReadOnlyList<IFlowDefinition>> GetAllFlowsAsync()
        => ValueTask.FromResult<IReadOnlyList<IFlowDefinition>>(_flows);
}
