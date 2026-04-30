namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Immutable audit record for a single execution attempt of a step.
/// Multiple attempts are created when a step is retried via the dashboard.
/// </summary>
public sealed class FlowStepAttemptRecord
{
    /// <summary>The run this attempt belongs to.</summary>
    public Guid RunId { get; set; }

    /// <summary>Step key (manifest key or runtime loop path).</summary>
    public string StepKey { get; set; } = default!;

    /// <summary>One-based attempt number (1 = first try, 2 = first retry, etc.).</summary>
    public int Attempt { get; set; }

    /// <summary>The step's handler type name at the time of this attempt.</summary>
    public string StepType { get; set; } = default!;

    /// <summary>Terminal status of this attempt.</summary>
    public string Status { get; set; } = default!;

    /// <summary>JSON-serialised inputs used for this attempt.</summary>
    public string? InputJson { get; set; }

    /// <summary>JSON-serialised output produced by this attempt, if any.</summary>
    public string? OutputJson { get; set; }

    /// <summary>Error message if this attempt failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Hangfire job ID for this attempt.</summary>
    public string? JobId { get; set; }

    /// <summary>UTC timestamp when this attempt started.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>UTC timestamp when this attempt reached a terminal status.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// JSON-serialised <see cref="Expressions.WhenEvaluationTrace"/> persisted when this
    /// attempt was skipped because a <c>When</c> clause evaluated to <see langword="false"/>;
    /// otherwise <see langword="null"/>.
    /// </summary>
    public string? EvaluationTraceJson { get; set; }
}
