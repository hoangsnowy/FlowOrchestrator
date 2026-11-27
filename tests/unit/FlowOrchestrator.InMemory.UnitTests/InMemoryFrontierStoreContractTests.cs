using FlowOrchestrator.InMemory;
using FlowOrchestrator.Testing.Shared;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// <see cref="FrontierStoreContract"/> against <see cref="InMemoryFlowRunStore"/>: the keyed status read, the
/// open-row check and the settled-iteration count, and the concurrent join and ForEach scenarios that rely on
/// them (#189 P0-1).
/// </summary>
public sealed class InMemoryFrontierStoreContractTests : FrontierStoreContract
{
    /// <inheritdoc/>
    protected override int JoinRepetitions => 200;

    internal override FrontierStores CreateStores()
    {
        var store = new InMemoryFlowRunStore();
        return new FrontierStores(store, store, store, new InMemoryOutputsRepository());
    }
}
