namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Persists per-job schedule overrides (pause state and cron expression overrides)
/// so they survive process restarts when <c>FlowSchedulerOptions.PersistOverrides</c> is enabled.
/// </summary>
public interface IFlowScheduleStateStore
{
    /// <summary>
    /// Returns the schedule state for the given Hangfire recurring job ID,
    /// or <see langword="null"/> if no override has been saved.
    /// </summary>
    Task<FlowScheduleState?> GetAsync(string jobId);

    /// <summary>Returns all persisted schedule states.</summary>
    Task<IReadOnlyList<FlowScheduleState>> GetAllAsync();

    /// <summary>Inserts or replaces the schedule state for <see cref="FlowScheduleState.JobId"/>.</summary>
    Task SaveAsync(FlowScheduleState state);

    /// <summary>Removes the schedule state for the given job ID if it exists.</summary>
    Task DeleteAsync(string jobId);
}
