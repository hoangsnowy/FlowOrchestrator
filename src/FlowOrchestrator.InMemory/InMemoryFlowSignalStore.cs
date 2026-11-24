using System.Collections.Concurrent;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.InMemory;

/// <summary>
/// In-process <see cref="IFlowSignalStore"/> backed by a <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// Used by tests and by the in-memory runtime; data does not survive process restart.
/// </summary>
/// <remarks>
/// Also an <see cref="IFlowRetentionStore"/>: the retention sweep drops the waiters of every run the
/// paired <see cref="InMemoryFlowRunStore"/> no longer holds, mirroring the SQL backends, which delete
/// <c>FlowSignalWaiters</c> rows together with their run. Before #189 nothing removed a waiter, so the
/// store grew for the lifetime of the process.
/// </remarks>
public sealed class InMemoryFlowSignalStore : IFlowSignalStore, IFlowRetentionStore
{
    private readonly ConcurrentDictionary<(Guid RunId, string StepKey), FlowSignalWaiter> _waiters = new();

    // Registration order, the tie-breaker for waiters with equal CreatedAt — the norm under a frozen
    // TimeProvider (FlowTestHostBuilder.WithSystemClock), where every waiter is stamped with the same instant.
    private readonly ConcurrentDictionary<(Guid RunId, string StepKey), long> _order = new();
    private long _sequence;

    private readonly InMemoryFlowRunStore? _runStore;
    private readonly TimeProvider _clock;

    /// <summary>Creates a standalone store; the retention sweep is a no-op without a paired run store.</summary>
    public InMemoryFlowSignalStore()
        : this(null, TimeProvider.System)
    {
    }

    /// <summary>Creates a store whose retention sweep follows <paramref name="runStore"/>'s run lifetime.</summary>
    /// <param name="runStore">The run store whose purged runs lose their waiters on the next sweep.</param>
    public InMemoryFlowSignalStore(InMemoryFlowRunStore runStore)
        : this(runStore, TimeProvider.System)
    {
    }

    /// <summary>Creates a standalone store that stamps and judges waiters by <paramref name="clock"/>.</summary>
    /// <param name="clock">
    /// The clock that stamps <see cref="FlowSignalWaiter.CreatedAt"/> / <see cref="FlowSignalWaiter.DeliveredAt"/>
    /// and decides whether a waiter's <see cref="FlowSignalWaiter.ExpiresAt"/> has passed.
    /// </param>
    public InMemoryFlowSignalStore(TimeProvider clock)
        : this(null, clock)
    {
    }

    /// <summary>
    /// Creates a store whose retention sweep follows <paramref name="runStore"/>'s run lifetime and whose
    /// waiters are stamped and judged by <paramref name="clock"/>.
    /// </summary>
    /// <param name="runStore">
    /// The run store whose purged runs lose their waiters on the next sweep; <see langword="null"/> makes the
    /// sweep a no-op.
    /// </param>
    /// <param name="clock">
    /// The clock that decides expiry. Pass the <see cref="TimeProvider"/> the <c>WaitForSignal</c> handler
    /// resolves — the DI container does this when one is registered — so both sides agree on when a waiter
    /// expired; a frozen test clock otherwise sets <see cref="FlowSignalWaiter.ExpiresAt"/> on one timeline
    /// while the store judges it on another.
    /// </param>
    public InMemoryFlowSignalStore(InMemoryFlowRunStore? runStore, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _runStore = runStore;
        _clock = clock;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Removes every waiter whose run no longer exists in the paired run store, so it follows the run
    /// store's own cutoff rather than applying <paramref name="cutoffUtc"/> a second time — a waiter
    /// has no completion time of its own. Order-independent: a waiter whose run is purged later in the
    /// same sweep goes on the next one. O(waiters) per sweep, off the request path.
    /// </remarks>
    public Task CleanupAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        if (_runStore is null)
        {
            return Task.CompletedTask;
        }

        foreach (var key in _waiters.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_runStore.ContainsRun(key.RunId))
            {
                _waiters.TryRemove(key, out _);
                _order.TryRemove(key, out _);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask RegisterWaiterAsync(
        Guid runId,
        string stepKey,
        string signalName,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        var key = (runId, stepKey);
        var now = _clock.GetUtcNow();
        _order.TryAdd(key, Interlocked.Increment(ref _sequence));
        _waiters.AddOrUpdate(
            key,
            _ => new FlowSignalWaiter
            {
                RunId = runId,
                StepKey = stepKey,
                SignalName = signalName,
                CreatedAt = now,
                ExpiresAt = expiresAt
            },
            (_, existing) =>
            {
                existing.SignalName = signalName;
                existing.ExpiresAt = expiresAt;
                return existing;
            });
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Delivers to the oldest UNDELIVERED, LIVE waiter for the run + signal name, mirroring the
    /// <c>DeliveredAt IS NULL AND (ExpiresAt IS NULL OR ExpiresAt &gt; now)</c> filter in the SQL backends.
    /// When several waiters share the same run + signal name (different step keys), a sequence of
    /// deliveries fans out to each live undelivered waiter in <see cref="FlowSignalWaiter.CreatedAt"/>
    /// (then registration) order rather than repeatedly re-selecting the first — already-delivered —
    /// waiter, and an expired waiter is skipped instead of blocking a live one behind it. Expiry is judged
    /// by this store's clock under the waiter's lock, so <see cref="FlowSignalWaiter.DeliveredAt"/> is
    /// always earlier than <see cref="FlowSignalWaiter.ExpiresAt"/>.
    /// </remarks>
    public ValueTask<SignalDeliveryResult> DeliverSignalAsync(
        Guid runId,
        string signalName,
        string payloadJson,
        CancellationToken ct = default)
    {
        // One pass over the store, no closure; the sort only covers this run's matches.
        List<FlowSignalWaiter>? candidates = null;
        foreach (var waiter in _waiters.Values)
        {
            if (waiter.RunId == runId && string.Equals(waiter.SignalName, signalName, StringComparison.OrdinalIgnoreCase))
            {
                (candidates ??= []).Add(waiter);
            }
        }

        if (candidates is null)
        {
            return ValueTask.FromResult(new SignalDeliveryResult(SignalDeliveryStatus.NotFound, null, null));
        }

        if (candidates.Count > 1)
        {
            candidates.Sort(CompareRegistrationOrder);
        }

        // Claim the oldest live waiter whose delivery slot is still open. Lock at the waiter level so
        // concurrent DeliverSignal calls racing for the SAME waiter are serialised; the post-lock
        // re-check skips any waiter another thread delivered between selection and lock acquisition.
        FlowSignalWaiter? firstExpired = null;
        foreach (var candidate in candidates)
        {
            if (candidate.DeliveredAt is not null)
            {
                continue;
            }

            lock (candidate)
            {
                if (candidate.DeliveredAt is not null)
                {
                    continue;
                }

                // Read the clock under the lock so the expiry decision and the DeliveredAt stamp are the same
                // instant. Inclusive boundary, like WaitForSignalHandler: now >= ExpiresAt is expired.
                var now = _clock.GetUtcNow();
                if (candidate.ExpiresAt is { } expiresAt && now >= expiresAt)
                {
                    firstExpired ??= candidate;
                    continue;
                }

                candidate.DeliveredAt = now;
                candidate.PayloadJson = payloadJson;
                return ValueTask.FromResult(
                    new SignalDeliveryResult(SignalDeliveryStatus.Delivered, candidate.StepKey, now));
            }
        }

        // No live waiter. An expired undelivered waiter is the one this signal was meant for — report that
        // rather than a duplicate delivery, matching the SQL backends' follow-up read.
        if (firstExpired is not null)
        {
            return ValueTask.FromResult(new SignalDeliveryResult(SignalDeliveryStatus.Expired, firstExpired.StepKey, null));
        }

        // Every matching waiter has already been delivered — report the oldest's prior delivery,
        // matching the SQL backends' "fall through to AlreadyDelivered" branch.
        var firstDelivered = candidates[0];
        return ValueTask.FromResult(
            new SignalDeliveryResult(SignalDeliveryStatus.AlreadyDelivered, firstDelivered.StepKey, firstDelivered.DeliveredAt));
    }

    /// <inheritdoc/>
    public ValueTask<FlowSignalWaiter?> GetWaiterAsync(Guid runId, string stepKey, CancellationToken ct = default)
    {
        _waiters.TryGetValue((runId, stepKey), out var waiter);
        return ValueTask.FromResult(waiter);
    }

    /// <inheritdoc/>
    public ValueTask RemoveWaiterAsync(Guid runId, string stepKey, CancellationToken ct = default)
    {
        _waiters.TryRemove((runId, stepKey), out _);
        _order.TryRemove((runId, stepKey), out _);
        return ValueTask.CompletedTask;
    }

    private int CompareRegistrationOrder(FlowSignalWaiter x, FlowSignalWaiter y)
    {
        var byCreated = x.CreatedAt.CompareTo(y.CreatedAt);
        if (byCreated != 0)
        {
            return byCreated;
        }

        _order.TryGetValue((x.RunId, x.StepKey), out var xs);
        _order.TryGetValue((y.RunId, y.StepKey), out var ys);
        return xs.CompareTo(ys);
    }
}
