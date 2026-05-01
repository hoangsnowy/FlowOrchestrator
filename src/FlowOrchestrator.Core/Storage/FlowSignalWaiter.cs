namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Persisted record of a <c>WaitForSignal</c> step that is currently parked, awaiting an external signal.
/// One row per (RunId, StepKey).
/// </summary>
public sealed class FlowSignalWaiter
{
    /// <summary>The run that owns the parked step.</summary>
    public Guid RunId { get; set; }

    /// <summary>The step key (matches the manifest step name).</summary>
    public string StepKey { get; set; } = default!;

    /// <summary>Logical signal name used to address this waiter from the signal endpoint.</summary>
    public string SignalName { get; set; } = default!;

    /// <summary>Time the waiter was first registered.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Optional absolute deadline; once <see cref="DateTimeOffset.UtcNow"/> passes this, the step fails.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Set when <see cref="IFlowSignalStore.DeliverSignalAsync"/> succeeds.</summary>
    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>JSON payload supplied by the caller when the signal was delivered.</summary>
    public string? PayloadJson { get; set; }
}

/// <summary>Outcome of a <see cref="IFlowSignalStore.DeliverSignalAsync"/> attempt.</summary>
public enum SignalDeliveryStatus
{
    /// <summary>Payload was persisted on the matching waiter.</summary>
    Delivered,

    /// <summary>No waiter is registered for the (run, signal name) pair.</summary>
    NotFound,

    /// <summary>A waiter exists but already received a signal — second delivery rejected.</summary>
    AlreadyDelivered
}

/// <summary>Result of <see cref="IFlowSignalStore.DeliverSignalAsync"/>.</summary>
/// <param name="Status">Disposition of the delivery attempt.</param>
/// <param name="StepKey">When <see cref="SignalDeliveryStatus.Delivered"/>, the step key whose waiter received the payload.</param>
/// <param name="DeliveredAt">When <see cref="SignalDeliveryStatus.Delivered"/>, the timestamp recorded on the waiter row.</param>
public readonly record struct SignalDeliveryResult(
    SignalDeliveryStatus Status,
    string? StepKey,
    DateTimeOffset? DeliveredAt);
