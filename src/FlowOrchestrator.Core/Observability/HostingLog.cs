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
}
