namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Persisted representation of a <see cref="FlowEvent"/>, with a monotonically
/// increasing sequence number for ordered retrieval via <see cref="IFlowEventReader"/>.
/// </summary>
public sealed class FlowEventRecord
{
    /// <summary>Monotonically increasing sequence number within a run, for stable ordering.</summary>
    public long Sequence { get; set; }

    /// <summary>The run this event belongs to.</summary>
    public Guid RunId { get; set; }

    /// <summary>UTC timestamp when the event was raised.</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Event type label (e.g. <c>"StepStarted"</c>, <c>"StepFailed"</c>).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The step key associated with this event, or <see langword="null"/> for run-level events.</summary>
    public string? StepKey { get; set; }

    /// <summary>Optional human-readable details about the event.</summary>
    public string? Message { get; set; }
}
