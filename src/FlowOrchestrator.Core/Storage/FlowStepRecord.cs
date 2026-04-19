namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Persisted state of a single step within a flow run, including its latest
/// execution outcome and the full attempt history.
/// </summary>
public sealed class FlowStepRecord
{
    /// <summary>The run this step belongs to.</summary>
    public Guid RunId { get; set; }

    /// <summary>Step key within the flow manifest (may be a runtime loop path such as <c>"processItems.0.validate"</c>).</summary>
    public string StepKey { get; set; } = default!;

    /// <summary>The step's handler type name.</summary>
    public string StepType { get; set; } = default!;

    /// <summary>Current status string (e.g. <c>"Succeeded"</c>, <c>"Failed"</c>, <c>"Running"</c>).</summary>
    public string Status { get; set; } = default!;

    /// <summary>JSON-serialised inputs resolved at execution time.</summary>
    public string? InputJson { get; set; }

    /// <summary>JSON-serialised output produced by the step handler on the latest successful attempt.</summary>
    public string? OutputJson { get; set; }

    /// <summary>Error message from the most recent failed attempt, if any.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Hangfire job ID of the most recent execution attempt.</summary>
    public string? JobId { get; set; }

    /// <summary>UTC timestamp of the first execution attempt.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>UTC timestamp of the last terminal outcome, or <see langword="null"/> if still running.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Total number of execution attempts, including retries.</summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Full attempt history. Populated only in detail views; <see langword="null"/> in list contexts.
    /// </summary>
    public IReadOnlyList<FlowStepAttemptRecord>? Attempts { get; set; }
}
