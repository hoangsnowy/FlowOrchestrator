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
    public ValueTask<SignalDeliveryResult> DeliverSignalAsync(
        Guid runId,
        string signalName,
        string payloadJson,
        CancellationToken ct = default)
    {
        var match = _waiters.Values
            .Where(w => w.RunId == runId &&
                        string.Equals(w.SignalName, signalName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(w => w.CreatedAt)
            .FirstOrDefault();

        if (match is null)
        {
            return ValueTask.FromResult(new SignalDeliveryResult(SignalDeliveryStatus.NotFound, null, null));
        }

        // Lock at the waiter level — concurrent DeliverSignal calls for the same step must be serialised.
        lock (match)
        {
            if (match.DeliveredAt is not null)
            {
                return ValueTask.FromResult(
                    new SignalDeliveryResult(SignalDeliveryStatus.AlreadyDelivered, match.StepKey, match.DeliveredAt));
            }

            var deliveredAt = DateTimeOffset.UtcNow;
            match.DeliveredAt = deliveredAt;
            match.PayloadJson = payloadJson;
            return ValueTask.FromResult(
                new SignalDeliveryResult(SignalDeliveryStatus.Delivered, match.StepKey, deliveredAt));
        }
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
