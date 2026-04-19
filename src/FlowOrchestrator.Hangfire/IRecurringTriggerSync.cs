namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Synchronises Hangfire recurring jobs for a flow's cron triggers at runtime
/// (e.g. after a pause/resume or cron override from the dashboard).
/// </summary>
public interface IRecurringTriggerSync
{
    /// <summary>
    /// Re-evaluates and updates Hangfire recurring jobs for all cron triggers of the given flow,
    /// applying any persisted schedule overrides from <c>IFlowScheduleStateStore</c>.
    /// </summary>
    /// <param name="flowId">The flow whose triggers should be synchronised.</param>
    /// <param name="isEnabled">When <see langword="false"/>, all recurring jobs for this flow are removed.</param>
    void SyncTriggers(Guid flowId, bool isEnabled);
}
