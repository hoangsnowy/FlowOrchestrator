namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Persisted schedule override state for a Hangfire recurring job.
/// Allows the dashboard to pause/resume schedules and override cron expressions
/// without modifying the code-defined flow manifest.
/// </summary>
public sealed class FlowScheduleState
{
    /// <summary>Hangfire recurring job ID (e.g. <c>"myflow-schedule"</c>).</summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>The flow this schedule belongs to.</summary>
    public Guid FlowId { get; set; }

    /// <summary>Display name of the flow at the time the override was saved.</summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>The manifest trigger key this schedule corresponds to (e.g. <c>"schedule"</c>).</summary>
    public string TriggerKey { get; set; } = string.Empty;

    /// <summary>
    /// When <see langword="true"/>, the recurring job is not enqueued on its cron schedule
    /// until explicitly resumed via the dashboard.
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Cron expression override; when set, supersedes the expression in the flow manifest.
    /// Set to <see langword="null"/> to revert to the manifest-defined schedule.
    /// </summary>
    public string? CronOverride { get; set; }

    /// <summary>UTC timestamp of the last save operation.</summary>
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
