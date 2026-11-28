namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Registry of the FlowOrchestrator processes ("servers") sharing a store: each one upserts a heartbeat row on
/// an interval, and the dashboard's Servers view lists them.
/// </summary>
/// <remarks>
/// <para>
/// Optional and separate from the run stores, so a custom storage backend that does not implement it keeps
/// working: without a registered implementation the heartbeat service does nothing and the dashboard reports
/// that the storage does not track servers. The SQL Server, PostgreSQL and in-memory backends implement it.
/// </para>
/// <para>
/// Timestamps (<see cref="FlowServerRecord.StartedAt"/>, <see cref="FlowServerRecord.LastHeartbeatAt"/>) come from
/// the store's own clock — the database server's for SQL backends — so every replica is measured against the
/// same clock, whatever the skew between the machines. Never on the step hot path: one write per heartbeat
/// interval per process.
/// </para>
/// </remarks>
public interface IFlowServerStore
{
    /// <summary>
    /// Inserts the server's row, or updates every column of an existing one except <see cref="FlowServerRecord.StartedAt"/>,
    /// and stamps <see cref="FlowServerRecord.LastHeartbeatAt"/> with the store's current time.
    /// </summary>
    /// <param name="server">The heartbeat; its <see cref="FlowServerRecord.StartedAt"/> and <see cref="FlowServerRecord.LastHeartbeatAt"/> are ignored.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the heartbeat is durable.</returns>
    Task UpsertServerAsync(FlowServerRecord server, CancellationToken cancellationToken = default);

    /// <summary>Returns every recorded server, most recent heartbeat first.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The servers; empty when none is recorded.</returns>
    Task<IReadOnlyList<FlowServerRecord>> GetServersAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes one server's row; called by a process on graceful shutdown.</summary>
    /// <param name="serverId">The <see cref="FlowServerRecord.ServerId"/> to remove.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns><see langword="true"/> when a row was removed; <see langword="false"/> when none matched.</returns>
    Task<bool> RemoveServerAsync(string serverId, CancellationToken cancellationToken = default);

    /// <summary>Removes every server whose latest heartbeat is older than <paramref name="notSeenFor"/>, by the store's clock.</summary>
    /// <param name="notSeenFor">Age of the latest heartbeat beyond which a server is considered gone; must be positive.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>The number of rows removed.</returns>
    Task<int> PurgeServersAsync(TimeSpan notSeenFor, CancellationToken cancellationToken = default);
}
