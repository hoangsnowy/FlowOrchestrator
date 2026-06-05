using System.Collections.Concurrent;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.InMemory;

/// <summary>
/// In-process <see cref="IFlowSignalStore"/> backed by a <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// Used by tests and by the in-memory runtime; data does not survive process restart.
/// </summary>
public sealed class InMemoryFlowSignalStore : IFlowSignalStore
{
    private readonly ConcurrentDictionary<(Guid RunId, string StepKey), FlowSignalWaiter> _waiters = new();

    /// <inheritdoc/>
    public ValueTask RegisterWaiterAsync(
        Guid runId,
        string stepKey,
        string signalName,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        var key = (runId, stepKey);
        var now = DateTimeOffset.UtcNow;
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
    /// Delivers to the oldest UNDELIVERED waiter for the run + signal name, mirroring the
    /// <c>AND delivered_at IS NULL</c> CTE filter in the SQL backends. When several waiters
    /// share the same run + signal name (different step keys), a sequence of deliveries fans
    /// out to each undelivered waiter in <see cref="FlowSignalWaiter.CreatedAt"/> order rather
    /// than repeatedly re-selecting the first — already-delivered — waiter.
    /// </remarks>
    public ValueTask<SignalDeliveryResult> DeliverSignalAsync(
        Guid runId,
        string signalName,
        string payloadJson,
        CancellationToken ct = default)
    {
        var candidates = _waiters.Values
            .Where(w => w.RunId == runId &&
                        string.Equals(w.SignalName, signalName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(w => w.CreatedAt)
            .ToList();

        if (candidates.Count == 0)
        {
            return ValueTask.FromResult(new SignalDeliveryResult(SignalDeliveryStatus.NotFound, null, null));
        }

        // Claim the oldest waiter whose delivery slot is still open. Lock at the waiter level so
        // concurrent DeliverSignal calls racing for the SAME waiter are serialised; the post-lock
        // re-check skips any waiter another thread delivered between selection and lock acquisition.
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

                var deliveredAt = DateTimeOffset.UtcNow;
                candidate.DeliveredAt = deliveredAt;
                candidate.PayloadJson = payloadJson;
                return ValueTask.FromResult(
                    new SignalDeliveryResult(SignalDeliveryStatus.Delivered, candidate.StepKey, deliveredAt));
            }
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
        return ValueTask.CompletedTask;
    }
}
