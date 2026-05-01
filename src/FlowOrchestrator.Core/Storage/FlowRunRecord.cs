namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Persisted record of a single flow run execution, including overall status and
/// an optional list of step records (populated by <see cref="IFlowRunStore.GetRunDetailAsync"/>).
/// </summary>
public sealed class FlowRunRecord
{
    /// <summary>Unique identifier for this run, generated at trigger time.</summary>
    public Guid Id { get; set; }

    /// <summary>The flow that this run belongs to.</summary>
    public Guid FlowId { get; set; }

    /// <summary>Display name of the flow at the time the run started.</summary>
    public string? FlowName { get; set; }

    /// <summary>Overall run status (e.g. <c>"Running"</c>, <c>"Succeeded"</c>, <c>"Failed"</c>).</summary>
    public string Status { get; set; } = default!;

    /// <summary>The manifest key of the trigger that started this run (e.g. <c>"schedule"</c>).</summary>
    public string? TriggerKey { get; set; }

    /// <summary>JSON-serialised trigger payload, stored for audit and retry.</summary>
    public string? TriggerDataJson { get; set; }

    /// <summary>HTTP headers from the trigger request, stored for audit and expression resolution.</summary>
    public IReadOnlyDictionary<string, string>? TriggerHeaders { get; set; }

    /// <summary>Hangfire job ID of the trigger job, for cross-referencing with the Hangfire dashboard.</summary>
    public string? BackgroundJobId { get; set; }

    /// <summary>UTC timestamp when the run was created.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>UTC timestamp when the run reached a terminal status, or <see langword="null"/> if still running.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Optional lineage pointer — when this run was created via "Re-run all" on a previous
    /// run, this is the ID of that previous run. <see langword="null"/> for original runs.
    /// </summary>
    public Guid? SourceRunId { get; set; }

    /// <summary>
    /// Step records for this run. Populated only by <see cref="IFlowRunStore.GetRunDetailAsync"/>;
    /// <see langword="null"/> in list-view results to avoid over-fetching.
    /// </summary>
    public IReadOnlyList<FlowStepRecord>? Steps { get; set; }
}
