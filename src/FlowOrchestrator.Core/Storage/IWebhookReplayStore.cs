namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Persistence contract for webhook replay-attack protection. Maintains a set
/// of <c>(flowId, triggerKey, nonce)</c> tuples with expiry; a successful
/// registration means the nonce has not been seen before, a conflict means a
/// replay attack is in progress.
/// </summary>
/// <remarks>
/// Storage-neutral by design — in-memory, Sql Server, and PostgreSQL impls
/// ship in their respective projects. Pick the backend that matches your
/// deployment topology: in-memory for single-replica, Sql/Postgres for
/// multi-replica coordination.
/// </remarks>
public interface IWebhookReplayStore
{
    /// <summary>
    /// Atomically records the nonce. Returns <see langword="true"/> when the
    /// nonce had not been seen for this flow + trigger; <see langword="false"/>
    /// when the same tuple is already present (replay).
    /// </summary>
    /// <param name="flowId">Flow identifier scoping the dedup window.</param>
    /// <param name="triggerKey">Trigger key scoping the dedup window.</param>
    /// <param name="nonce">Unique-per-event token (timestamp + delivery-id, etc.).</param>
    /// <param name="expiresAt">When the entry may be purged.</param>
    /// <param name="ct">Cancellation propagated from the host pipeline.</param>
    ValueTask<bool> TryRegisterAsync(
        Guid flowId,
        string triggerKey,
        string nonce,
        DateTimeOffset expiresAt,
        CancellationToken ct = default);

    /// <summary>Drops every entry whose <c>ExpiresAt</c> is at or before <paramref name="now"/>.</summary>
    /// <param name="now">Reference instant; entries with <c>ExpiresAt &lt;= now</c> are removed.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct = default);
}
