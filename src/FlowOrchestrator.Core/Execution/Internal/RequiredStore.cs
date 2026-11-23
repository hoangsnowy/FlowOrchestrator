namespace FlowOrchestrator.Core.Execution.Internal;

/// <summary>
/// Picks the storage service a component binds to from its DI registrations, failing fast when the
/// configured storage backend did not register one.
/// </summary>
/// <remarks>
/// Consumers take the whole sequence and bind to the FIRST registration so that every component —
/// the engine, the signal dispatcher, the recovery service — reads and claims against the same store
/// even when a consumer layers a decorator registration on top of the built-in one. A single-service
/// resolution would bind to the LAST registration instead and split the components across stores.
/// </remarks>
internal static class RequiredStore
{
    /// <summary>Returns the first registered <typeparamref name="TStore"/>.</summary>
    /// <typeparam name="TStore">The storage contract the consumer requires.</typeparam>
    /// <param name="stores">Every registration of <typeparamref name="TStore"/>, in registration order.</param>
    /// <param name="consumer">The component requiring the store; named in the error message.</param>
    /// <exception cref="InvalidOperationException">Thrown when no <typeparamref name="TStore"/> is registered.</exception>
    public static TStore First<TStore>(IEnumerable<TStore> stores, string consumer)
        where TStore : class
    {
        ArgumentNullException.ThrowIfNull(stores);

        foreach (var store in stores)
        {
            return store;
        }

        throw new InvalidOperationException(
            $"{consumer} requires an {typeof(TStore).Name} registration, but none was found. " +
            "Configure a storage backend (UseSqlServer, UsePostgreSql or UseInMemory); a custom backend " +
            $"must register its {typeof(TStore).Name} implementation alongside IFlowRunStore.");
    }
}
