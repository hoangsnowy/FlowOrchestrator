namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Synchronises the runtime scheduler's recurring jobs for a flow's cron triggers
/// (e.g. after a pause/resume or cron override from the dashboard).
/// </summary>
/// <remarks>
/// <c>AddFlowOrchestrator</c> registers the single runtime-agnostic implementation,
/// <see cref="RecurringTriggerSync"/>, which resolves the effective cron (manifest, persisted
/// override, pause state) once and drives whichever <see cref="IRecurringTriggerDispatcher"/>
/// the configured runtime provides. Runtimes no longer implement this interface themselves.
/// </remarks>
public interface IRecurringTriggerSync
{
    /// <summary>
    /// Re-evaluates and updates all recurring jobs for the given flow,
    /// applying any persisted schedule overrides from <c>IFlowScheduleStateStore</c>.
    /// </summary>
    /// <param name="flowId">The flow whose triggers should be synchronised.</param>
    /// <param name="isEnabled">When <see langword="false"/>, all recurring jobs for this flow are removed.</param>
    /// <param name="cancellationToken">Cancels the schedule-state lookups.</param>
    /// <returns>A task that completes once every cron trigger of the flow has been registered or removed.</returns>
    Task SyncTriggersAsync(Guid flowId, bool isEnabled, CancellationToken cancellationToken = default);
}
