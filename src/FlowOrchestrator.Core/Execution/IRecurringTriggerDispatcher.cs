namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Bridges the engine to the recurring-job scheduler (Hangfire, Quartz, etc.)
/// for cron-based flow triggers. Implementations live in the runtime adapter package;
/// the Core engine and Dashboard only depend on this interface.
/// </summary>
public interface IRecurringTriggerDispatcher
{
    /// <summary>Creates or updates a recurring cron job that calls <c>TriggerByScheduleAsync</c>.</summary>
    /// <param name="jobId">The scheduler's stable job identifier (e.g. <c>"flow-{id}-{triggerKey}"</c>).</param>
    /// <param name="flowId">The flow to trigger.</param>
    /// <param name="triggerKey">The trigger key within the flow manifest.</param>
    /// <param name="cronExpression">Standard cron expression controlling firing frequency.</param>
    void RegisterOrUpdate(string jobId, Guid flowId, string triggerKey, string cronExpression);

    /// <summary>Removes a recurring job, stopping future executions.</summary>
    /// <param name="jobId">The job identifier used when the job was registered.</param>
    void Remove(string jobId);

    /// <summary>Immediately triggers the next execution of a recurring job without altering its schedule.</summary>
    /// <param name="jobId">The job identifier of the recurring job to trigger.</param>
    void TriggerOnce(string jobId);

    /// <summary>
    /// Enqueues a one-time immediate trigger for a flow identified by <paramref name="flowId"/>
    /// and <paramref name="triggerKey"/>. Used when a paused schedule is manually fired.
    /// </summary>
    Task EnqueueTriggerAsync(Guid flowId, string triggerKey, CancellationToken ct = default);
}
