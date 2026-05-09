using System.Collections.Concurrent;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Webhooks.Dlq;

/// <summary>
/// In-process bounded ring buffer for webhook receive entries. Last
/// <see cref="MaxEntries"/> rows kept; older rows drop off as new ones are
/// appended. Suitable for single-replica deployments.
/// </summary>
/// <remarks>
/// Multi-replica + long-retention storage requires a Sql / Postgres backend.
/// The interface stays unchanged so the upgrade is a pure DI swap.
/// </remarks>
public sealed class InMemoryWebhookRejectionStore : IWebhookRejectionStore
{
    /// <summary>Default ring-buffer capacity.</summary>
    public const int DefaultMaxEntries = 1_000;

    private readonly int _maxEntries;
    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private readonly LinkedList<WebhookRejectionRecord> _entries = new();
    private long _nextId;

    /// <summary>Creates the store with the default capacity.</summary>
    public InMemoryWebhookRejectionStore() : this(DefaultMaxEntries, TimeProvider.System)
    {
    }

    /// <summary>Creates the store with a custom capacity + clock (used by tests).</summary>
    /// <param name="maxEntries">Ring-buffer capacity.</param>
    /// <param name="clock">Time provider for retention queries.</param>
    public InMemoryWebhookRejectionStore(int maxEntries, TimeProvider clock)
    {
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        _maxEntries = maxEntries;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Current entry count (rejected + accepted).</summary>
    public int MaxEntries => _maxEntries;

    /// <inheritdoc/>
    public ValueTask WriteAsync(WebhookRejectionRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            var withId = record with { Id = ++_nextId };
            _entries.AddFirst(withId);
            while (_entries.Count > _maxEntries)
                _entries.RemoveLast();
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<WebhookRejectionRecord>> QueryRecentAsync(
        Guid? flowId,
        string? reason,
        bool includeAccepted,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 50;
        if (take > 500) take = 500;
        WebhookRejectionRecord[] snapshot;
        lock (_lock)
        {
            snapshot = _entries.ToArray();
        }
        var filtered = snapshot
            .Where(r => (includeAccepted || !r.IsAccepted)
                        && (flowId is null || r.FlowId == flowId)
                        && (string.IsNullOrEmpty(reason) || string.Equals(r.Reason, reason, StringComparison.OrdinalIgnoreCase)))
            .Skip(skip)
            .Take(take)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<WebhookRejectionRecord>>(filtered);
    }

    /// <inheritdoc/>
    public ValueTask<WebhookRejectionPage> QueryAsync(WebhookRejectionQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var skip = query.Skip < 0 ? 0 : query.Skip;
        var take = query.Take <= 0 ? 50 : (query.Take > 500 ? 500 : query.Take);
        WebhookRejectionRecord[] snapshot;
        lock (_lock)
        {
            snapshot = _entries.ToArray();
        }
        var search = query.Search;
        var filtered = snapshot
            .Where(r => (query.IncludeAccepted || !r.IsAccepted)
                        && (query.IncludeRejected || r.IsAccepted)
                        && (query.FlowId is null || r.FlowId == query.FlowId)
                        && (string.IsNullOrEmpty(query.Reason) || string.Equals(r.Reason, query.Reason, StringComparison.OrdinalIgnoreCase))
                        && (string.IsNullOrEmpty(search)
                            || (r.Reason?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                            || (r.TriggerKey?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                            || (r.RemoteIp?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)))
            .ToArray();
        var page = filtered.Skip(skip).Take(take).ToArray();
        return ValueTask.FromResult(new WebhookRejectionPage(page, filtered.Length));
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyDictionary<string, long>> CountsByReasonAsync(TimeSpan window, CancellationToken ct = default)
    {
        var cutoff = _clock.GetUtcNow() - window;
        WebhookRejectionRecord[] snapshot;
        lock (_lock)
        {
            snapshot = _entries.ToArray();
        }
        var counts = snapshot
            .Where(r => r.ReceivedAt >= cutoff && !string.IsNullOrEmpty(r.Reason))
            .GroupBy(r => r.Reason, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.OrdinalIgnoreCase);
        return ValueTask.FromResult<IReadOnlyDictionary<string, long>>(counts);
    }
}
