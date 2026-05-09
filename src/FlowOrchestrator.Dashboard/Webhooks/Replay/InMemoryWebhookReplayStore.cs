using System.Collections.Concurrent;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Webhooks.Replay;

/// <summary>
/// In-process <see cref="IWebhookReplayStore"/> backed by a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>. Suitable for single-
/// replica deployments — multi-replica coordination requires a shared backend.
/// </summary>
/// <remarks>
/// Entries are not garbage-collected eagerly; <see cref="PurgeExpiredAsync"/>
/// must be called on a schedule (the <see cref="WebhookReplayJanitor"/>
/// background service drives this once per minute).
/// </remarks>
public sealed class InMemoryWebhookReplayStore : IWebhookReplayStore
{
    private readonly ConcurrentDictionary<NonceKey, DateTimeOffset> _entries = new();

    /// <inheritdoc/>
    public ValueTask<bool> TryRegisterAsync(
        Guid flowId,
        string triggerKey,
        string nonce,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        var key = new NonceKey(flowId, triggerKey, nonce);
        var added = _entries.TryAdd(key, expiresAt);
        return ValueTask.FromResult(added);
    }

    /// <inheritdoc/>
    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var purged = 0;
        foreach (var (key, expires) in _entries)
        {
            if (expires <= now && _entries.TryRemove(key, out _))
                purged++;
        }
        return ValueTask.FromResult(purged);
    }

    private readonly record struct NonceKey(Guid FlowId, string TriggerKey, string Nonce);
}
