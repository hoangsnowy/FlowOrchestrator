namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Read-only view of the scheduler's registered recurring trigger jobs.
/// Allows the Dashboard to list cron schedules without a direct Hangfire dependency.
/// </summary>
public interface IRecurringTriggerInspector
{
    /// <summary>Returns metadata about all currently registered recurring trigger jobs.</summary>
    Task<IReadOnlyList<RecurringTriggerInfo>> GetJobsAsync();
}

/// <summary>
/// Runtime metadata snapshot for a single recurring trigger job.
/// </summary>
/// <param name="Id">The stable job identifier in the scheduler.</param>
/// <param name="Cron">The active cron expression.</param>
/// <param name="NextExecution">UTC time of the next scheduled execution, or <see langword="null"/> if not yet computed.</param>
/// <param name="LastExecution">UTC time of the most recent execution, or <see langword="null"/> if never fired.</param>
/// <param name="LastJobId">Scheduler-assigned ID of the most recent one-off job, or <see langword="null"/>.</param>
/// <param name="LastJobState">Status of the most recent job (e.g. <c>"Succeeded"</c>, <c>"Failed"</c>).</param>
/// <param name="TimeZoneId">IANA time-zone identifier for cron evaluation, or <see langword="null"/> for UTC.</param>
public sealed record RecurringTriggerInfo(
    string Id,
    string Cron,
    DateTime? NextExecution,
    DateTime? LastExecution,
    string? LastJobId,
    string? LastJobState,
    string? TimeZoneId);
