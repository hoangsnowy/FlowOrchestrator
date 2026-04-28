namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Synchronises the runtime scheduler's recurring jobs for a flow's cron triggers
/// (e.g. after a pause/resume or cron override from the dashboard).
/// </summary>
public interface IRecurringTriggerSync
{
    /// <summary>
    /// Re-evaluates and updates all recurring jobs for the given flow,
    /// applying any persisted schedule overrides from <c>IFlowScheduleStateStore</c>.
    /// </summary>
    /// <param name="flowId">The flow whose triggers should be synchronised.</param>
    /// <param name="isEnabled">When <see langword="false"/>, all recurring jobs for this flow are removed.</param>
    void SyncTriggers(Guid flowId, bool isEnabled);
}
