namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// In-memory event raised during a flow run execution.
/// Events are persisted via <see cref="IOutputsRepository.RecordEventAsync"/> and
/// stored as <see cref="FlowEventRecord"/> when event persistence is enabled.
/// </summary>
public sealed class FlowEvent
{
    /// <summary>UTC timestamp when the event was raised.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Event type label (e.g. <c>"StepStarted"</c>, <c>"StepFailed"</c>, <c>"RunCancelled"</c>).</summary>
    public string Type { get; init; } = default!;

    /// <summary>The step key associated with this event, or <see langword="null"/> for run-level events.</summary>
    public string? StepKey { get; init; }

    /// <summary>Optional human-readable details about the event.</summary>
    public string? Message { get; init; }
}
