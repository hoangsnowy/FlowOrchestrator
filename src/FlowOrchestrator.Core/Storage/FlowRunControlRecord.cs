namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Control-plane state for a run: timeout deadline, cancellation request, and idempotency key.
/// Written before the run starts and polled by step handlers to honour cancellation signals.
/// </summary>
public sealed class FlowRunControlRecord
{
    /// <summary>The run this record controls.</summary>
    public Guid RunId { get; set; }

    /// <summary>The flow that owns this run.</summary>
    public Guid FlowId { get; set; }

    /// <summary>The manifest trigger key used to start this run.</summary>
    public string TriggerKey { get; set; } = string.Empty;

    /// <summary>
    /// Caller-supplied idempotency key, or <see langword="null"/> if not provided.
    /// Used to deduplicate trigger invocations with the same key within a flow/trigger combination.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>UTC timestamp when the control record was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Absolute UTC deadline after which the run should be terminated.
    /// <see langword="null"/> means no timeout is enforced.
    /// </summary>
    public DateTimeOffset? TimeoutAtUtc { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the run has received a cancellation request.
    /// Step handlers should check this flag and exit cleanly as soon as possible.
    /// </summary>
    public bool CancelRequested { get; set; }

    /// <summary>Optional human-readable reason for the cancellation request.</summary>
    public string? CancelReason { get; set; }

    /// <summary>UTC timestamp when the cancellation was requested, if applicable.</summary>
    public DateTimeOffset? CancelRequestedAtUtc { get; set; }

    /// <summary>UTC timestamp when the run was marked as timed out, if applicable.</summary>
    public DateTimeOffset? TimedOutAtUtc { get; set; }
}
