namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// One entry of a run's chronological timeline, as returned by <see cref="FlowRunTimeline.Build"/>.
/// </summary>
/// <remarks>
/// Since 2.0 these entries are no longer persisted: they are derived on read from the run record
/// and its step attempts (<see cref="FlowRunRecord"/>, <see cref="FlowStepRecord.Attempts"/>), so
/// producing them costs nothing on the step-execution path. The type keeps its 1.x name and shape
/// so code that consumed the old persisted event log — and the dashboard's
/// <c>/api/runs/{runId}/events</c> JSON — keeps working.
/// </remarks>
public sealed class FlowEventRecord
{
    /// <summary>1-based position of the entry within the run's timeline, in chronological order.</summary>
    public long Sequence { get; set; }

    /// <summary>The run this entry belongs to.</summary>
    public Guid RunId { get; set; }

    /// <summary>UTC time of the state change the entry describes (a run or attempt start or completion).</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// Entry kind: <c>run.started</c>, <c>run.completed</c>, <c>step.started</c>,
    /// <c>step.completed</c>, <c>step.failed</c>, <c>step.skipped</c> or <c>step.pending</c>
    /// (see the constants on <see cref="FlowRunTimeline"/>).
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The step key the entry describes, or <see langword="null"/> for run-level entries.</summary>
    public string? StepKey { get; set; }

    /// <summary>Human-readable detail, e.g. the failure reason or the skip reason recorded on the step.</summary>
    public string? Message { get; set; }
}
