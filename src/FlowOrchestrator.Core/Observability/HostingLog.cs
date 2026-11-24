using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Observability;

/// <summary>
/// Source-generated <see cref="LoggerMessage"/> methods for the runtime-agnostic hosted services
/// (flow sync, recurring-trigger sync, retention sweep). Event IDs use the 5xxx block, reserved for
/// hosting and scheduling.
/// </summary>
internal static partial class HostingLog
{
    [LoggerMessage(EventId = 5000, Level = LogLevel.Information, Message = "Synced flow {FlowName} ({FlowId}) to store.")]
    public static partial void FlowSynced(ILogger logger, string flowName, Guid flowId);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning, Message = "Failed to sync flow {FlowId}.")]
    public static partial void FlowSyncFailed(ILogger logger, Exception ex, Guid flowId);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Debug, Message = "Recurring job {JobId} synced with effective cron '{Cron}'.")]
    public static partial void RecurringJobSynced(ILogger logger, string jobId, string cron);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Debug, Message = "Recurring job {JobId} removed: flow disabled or trigger has no cron expression.")]
    public static partial void RecurringJobRemoved(ILogger logger, string jobId);

    [LoggerMessage(EventId = 5004, Level = LogLevel.Information, Message = "Recurring job {JobId} remains paused.")]
    public static partial void RecurringJobPaused(ILogger logger, string jobId);

    [LoggerMessage(EventId = 5005, Level = LogLevel.Warning, Message = "Flow retention cleanup failed for store {StoreType}.")]
    public static partial void RetentionCleanupFailed(ILogger logger, Exception ex, string storeType);

    [LoggerMessage(EventId = 5010, Level = LogLevel.Warning, Message = "Periodic timeout-enforcement sweep failed; will retry on the next tick.")]
    public static partial void TimeoutSweepFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 5011, Level = LogLevel.Warning, Message = "Recurring job {JobId}: the persisted cron override '{CronOverride}' was rejected by the runtime; scheduling on the manifest cron '{ManifestCron}' instead. Fix or clear the override from the dashboard.")]
    public static partial void RecurringJobOverrideRejected(ILogger logger, Exception ex, string jobId, string cronOverride, string manifestCron);

    // ── Startup recovery (FlowRunRecoveryHostedService / RunRecoverer): 5020-5039 ─────────────────────

    [LoggerMessage(EventId = 5020, Level = LogLevel.Warning, Message = "FlowRunRecoveryHostedService: released {Count} orphaned claim(s) with no step status for run {RunId}: {Keys}.")]
    public static partial void RecoveryReleasedOrphanedClaims(ILogger logger, int count, Guid runId, string keys);

    [LoggerMessage(EventId = 5021, Level = LogLevel.Information, Message = "FlowRunRecoveryHostedService: settled {Count} completed loop step(s) for run {RunId}: {Keys}.")]
    public static partial void RecoverySettledLoops(ILogger logger, int count, Guid runId, string keys);

    [LoggerMessage(EventId = 5022, Level = LogLevel.Information, Message = "FlowRunRecoveryHostedService: re-dispatched ready step '{StepKey}' for run {RunId}.")]
    public static partial void RecoveryRedispatchedReadyStep(ILogger logger, string stepKey, Guid runId);

    [LoggerMessage(EventId = 5023, Level = LogLevel.Information, Message = "FlowRunRecoveryHostedService: skipped ready step '{StepKey}' for run {RunId} — its When clause is false.")]
    public static partial void RecoverySkippedWhenFalse(ILogger logger, string stepKey, Guid runId);

    [LoggerMessage(EventId = 5024, Level = LogLevel.Information, Message = "FlowRunRecoveryHostedService: skipped blocked step '{StepKey}' for run {RunId} — its runAfter prerequisites were not met.")]
    public static partial void RecoverySkippedBlockedStep(ILogger logger, string stepKey, Guid runId);

    [LoggerMessage(EventId = 5025, Level = LogLevel.Information, Message = "FlowRunRecoveryHostedService: admitted loop '{LoopKey}' iteration {Index} for run {RunId} — enqueued '{StepKey}'.")]
    public static partial void RecoveryAdmittedLoopIteration(ILogger logger, string loopKey, int index, Guid runId, string stepKey);

    [LoggerMessage(EventId = 5026, Level = LogLevel.Error, Message = "FlowRunRecoveryHostedService: dispatching step '{StepKey}' of run {RunId} failed; its ledger row was released and the run's other steps are still recovered. The next recovery pass retries it.")]
    public static partial void RecoveryDispatchFailed(ILogger logger, Exception ex, string stepKey, Guid runId);

    [LoggerMessage(EventId = 5027, Level = LogLevel.Error, Message = "FlowRunRecoveryHostedService: dispatching step '{StepKey}' failed and its dispatch-ledger row could not be released.")]
    public static partial void RecoveryDispatchReleaseFailed(ILogger logger, Exception ex, string stepKey);

    [LoggerMessage(EventId = 5028, Level = LogLevel.Warning, Message = "FlowRunRecoveryHostedService: failed to annotate dispatch for step '{StepKey}'.")]
    public static partial void RecoveryDispatchAnnotateFailed(ILogger logger, Exception ex, string stepKey);

    [LoggerMessage(EventId = 5029, Level = LogLevel.Debug, Message = "FlowRunRecoveryHostedService: run {RunId} — no orphaned steps detected.")]
    public static partial void RecoveryNothingOrphaned(ILogger logger, Guid runId);

    [LoggerMessage(EventId = 5030, Level = LogLevel.Information, Message = "FlowRunRecoveryHostedService: closed zombie run {RunId} as {Status} (all steps terminal but run remained Running).")]
    public static partial void RecoveryClosedZombieRun(ILogger logger, Guid runId, string status);

    [LoggerMessage(EventId = 5031, Level = LogLevel.Warning, Message = "FlowRunRecoveryHostedService: failed to close zombie run {RunId}.")]
    public static partial void RecoveryZombieCloseFailed(ILogger logger, Exception ex, Guid runId);

    [LoggerMessage(EventId = 5032, Level = LogLevel.Warning, Message = "FlowRunRecoveryHostedService: could not restore trigger data for run {RunId}.")]
    public static partial void RecoveryTriggerDataUnavailable(ILogger logger, Exception ex, Guid runId);

    [LoggerMessage(EventId = 5033, Level = LogLevel.Information, Message = "FlowRunRecoveryHostedService: re-enqueued step '{StepKey}' for run {RunId} — it was queued on a runtime whose queue does not survive a restart.")]
    public static partial void RecoveryRedeliveredLostStep(ILogger logger, string stepKey, Guid runId);

    [LoggerMessage(EventId = 5034, Level = LogLevel.Error, Message = "FlowRunRecoveryHostedService: re-enqueuing step '{StepKey}' of run {RunId} failed; its ledger row is kept and the next startup retries.")]
    public static partial void RecoveryRedeliveryFailed(ILogger logger, Exception ex, string stepKey, Guid runId);
}
