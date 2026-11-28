using System.Collections.Concurrent;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.InMemory;

/// <summary>
/// Process-local <see cref="IFlowServerStore"/>: the in-memory backend is not shared between processes, so it only
/// ever lists the process it lives in (and anything a test writes into it).
/// </summary>
/// <remarks>Timestamps come from the injected <see cref="TimeProvider"/>, the in-memory counterpart of a database clock.</remarks>
internal sealed class InMemoryFlowServerStore : IFlowServerStore
{
    private readonly ConcurrentDictionary<string, FlowServerRecord> _servers = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    /// <summary>Creates the store.</summary>
    /// <param name="clock">Stamps <see cref="FlowServerRecord.StartedAt"/> and <see cref="FlowServerRecord.LastHeartbeatAt"/>, and ages rows for the purge.</param>
    public InMemoryFlowServerStore(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <inheritdoc/>
    public Task UpsertServerAsync(FlowServerRecord server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(server.ServerId);
        var now = _clock.GetUtcNow();
        _servers.AddOrUpdate(
            server.ServerId,
            static (_, state) => state.Server with { StartedAt = state.Now, LastHeartbeatAt = state.Now },
            static (_, existing, state) => state.Server with { StartedAt = existing.StartedAt, LastHeartbeatAt = state.Now },
            (Server: server, Now: now));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<FlowServerRecord>> GetServersAsync(CancellationToken cancellationToken = default)
    {
        var observedAt = _clock.GetUtcNow();
        var servers = _servers.Values.ToArray();
        for (var i = 0; i < servers.Length; i++)
        {
            servers[i] = servers[i] with { ObservedAt = observedAt };
        }

        Array.Sort(servers, static (a, b) => b.LastHeartbeatAt.CompareTo(a.LastHeartbeatAt));
        return Task.FromResult<IReadOnlyList<FlowServerRecord>>(servers);
    }

    /// <inheritdoc/>
    public Task<bool> RemoveServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverId);
        return Task.FromResult(_servers.TryRemove(serverId, out _));
    }

    /// <inheritdoc/>
    public Task<int> PurgeServersAsync(TimeSpan notSeenFor, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(notSeenFor, TimeSpan.Zero);
        var cutoff = _clock.GetUtcNow() - notSeenFor;
        var removed = 0;
        foreach (var (id, server) in _servers)
        {
            // Removes the row only if it is still the one judged stale: a heartbeat landing meanwhile keeps it.
            if (server.LastHeartbeatAt < cutoff && _servers.TryRemove(new KeyValuePair<string, FlowServerRecord>(id, server)))
            {
                removed++;
            }
        }

        return Task.FromResult(removed);
    }
}
