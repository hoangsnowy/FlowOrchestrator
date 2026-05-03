using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Observability;

/// <summary>
/// Source-generated <see cref="LoggerMessage"/> methods for every <see cref="ILogger"/> call
/// emitted by <c>FlowOrchestratorEngine</c>. Generates allocation-free, AOT-friendly delegates
/// at build time and gives every call site a stable <see cref="EventId"/>.
/// </summary>
/// <remarks>
/// Event IDs are kept in numeric sync with <see cref="LogEvents"/> so consumers can choose either
/// API surface — programmatic filtering by <c>EventId.Id</c> on the structured-log sink, or the
/// strongly-typed methods here at the call site. Keep both files in lock-step when adding new
/// events: bump the numeric ID, add the <see cref="LogEvents"/> constant, and add the matching
/// <see cref="LoggerMessageAttribute"/> partial method below.
/// </remarks>
internal static partial class EngineLog
{
    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Failed to track flow run start.")]
    public static partial void RunStartTrackingFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error, Message = "Step execution failed for {StepKey}")]
    public static partial void StepExecutionFailed(ILogger logger, Exception ex, string stepKey);

    [LoggerMessage(EventId = 2005, Level = LogLevel.Error, Message = "When-clause evaluation failed for step {StepKey}; treating as failure.")]
    public static partial void WhenEvaluationFailed(ILogger logger, Exception ex, string stepKey);

    [LoggerMessage(EventId = 2006, Level = LogLevel.Warning, Message = "Failed to track step start.")]
    public static partial void StepStartTrackingFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2007, Level = LogLevel.Warning, Message = "Failed to track step completion.")]
    public static partial void StepCompletionTrackingFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2008, Level = LogLevel.Warning, Message = "Failed to mark step {StepKey} as skipped due to terminal run status.")]
    public static partial void StepSkipTrackingFailed(ILogger logger, Exception ex, string stepKey);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Failed to annotate dispatch for step {StepKey}.")]
    public static partial void DispatchAnnotateFailed(ILogger logger, Exception ex, string stepKey);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "Failed to record flow event {EventType}.")]
    public static partial void EventPersistenceFailed(ILogger logger, Exception ex, string eventType);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "Realtime event notifier threw while publishing {EventType}; engine continuing.")]
    public static partial void EventNotifierFailed(ILogger logger, Exception ex, string eventType);

    [LoggerMessage(EventId = 9000, Level = LogLevel.Debug, Message = "No IFlowRunRuntimeStore registered. Running in legacy sequential mode — parallel graph evaluation and step-claim deduplication are disabled.")]
    public static partial void LegacySequentialMode(ILogger logger);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Warning, Message = "Trigger rejected: flow {FlowId} ('{TriggerKey}') is disabled.")]
    public static partial void TriggerRejectedDisabledFlow(ILogger logger, Guid flowId, string triggerKey);
}
