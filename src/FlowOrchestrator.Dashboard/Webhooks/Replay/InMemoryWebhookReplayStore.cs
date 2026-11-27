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
    private readonly ConcurrentDictionary<NonceKey, Entry> _entries = new();

    /// <inheritdoc/>
    public ValueTask<bool> TryRegisterAsync(
        Guid flowId,
        string triggerKey,
        string nonce,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
        => TryRegisterAsync(flowId, triggerKey, nonce, expiresAt, Guid.NewGuid(), ct);

    /// <inheritdoc/>
    public ValueTask<bool> TryRegisterAsync(
        Guid flowId,
        string triggerKey,
        string nonce,
        DateTimeOffset expiresAt,
        Guid registrationId,
        CancellationToken ct = default)
    {
        // Same bound as the SQL stores, so a long nonce maps to the same key on every backend.
        var key = new NonceKey(flowId, triggerKey, WebhookReplayNonceStorage.ToStorageForm(nonce));
        var added = _entries.TryAdd(key, new Entry(expiresAt, registrationId));
        return ValueTask.FromResult(added);
    }

    /// <inheritdoc/>
    public ValueTask<bool> TryForgetAsync(
        Guid flowId,
        string triggerKey,
        string nonce,
        Guid registrationId,
        CancellationToken ct = default)
    {
        var key = new NonceKey(flowId, triggerKey, WebhookReplayNonceStorage.ToStorageForm(nonce));
        if (registrationId == Guid.Empty
            || !_entries.TryGetValue(key, out var entry)
            || entry.RegistrationId != registrationId)
            return ValueTask.FromResult(false);

        // Compare-and-remove: removes the pair only if the value is still the one this owner registered.
        return ValueTask.FromResult(_entries.TryRemove(new KeyValuePair<NonceKey, Entry>(key, entry)));
    }

    /// <inheritdoc/>
    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var purged = 0;
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAt <= now && _entries.TryRemove(new KeyValuePair<NonceKey, Entry>(key, entry)))
                purged++;
        }
        return ValueTask.FromResult(purged);
    }

    private readonly record struct NonceKey(Guid FlowId, string TriggerKey, string Nonce);

    private readonly record struct Entry(DateTimeOffset ExpiresAt, Guid RegistrationId);
}
