using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Observability;

/// <summary>
/// Source-generated <see cref="LoggerMessage"/> methods for the signal-delivery path
/// (<c>FlowSignalDispatcher</c>). Mirrors the <see cref="EngineLog"/> pattern: allocation-free,
/// AOT-friendly delegates with stable <see cref="EventId"/> values.
/// </summary>
/// <remarks>
/// Both events here describe failures that are deliberately swallowed, because the signal itself is
/// already durably persisted before the nudge is attempted and the caller is told the delivery
/// succeeded. Swallowing them silently would be wrong: a lost nudge leaves the step parked until its
/// safety-net invocation, which is 24 hours away when the step declares no <c>timeoutSeconds</c>.
/// Event IDs use the 4xxx block, reserved for signal delivery.
/// </remarks>
internal static partial class SignalLog
{
    /// <summary>Logs a resume nudge that could not be handed to the runtime.</summary>
    /// <param name="logger">Logger, or <see langword="null"/> when none was injected.</param>
    /// <param name="ex">The dispatcher failure.</param>
    /// <param name="runId">Run owning the parked step.</param>
    /// <param name="stepKey">Step that will not be woken by this nudge.</param>
    /// <param name="delayed">Whether the delayed path was taken rather than the immediate one.</param>
    public static void ResumeDispatchFailed(ILogger? logger, Exception ex, Guid runId, string stepKey, bool delayed)
    {
        if (logger is not null)
        {
            ResumeDispatchFailedCore(logger, ex, runId, stepKey, delayed);
        }
    }

    /// <summary>Logs a claim-state read that failed, forcing the slower delayed resume path.</summary>
    /// <param name="logger">Logger, or <see langword="null"/> when none was injected.</param>
    /// <param name="ex">The claim-store failure.</param>
    /// <param name="runId">Run owning the parked step.</param>
    /// <param name="stepKey">Step whose claim state could not be read.</param>
    public static void ClaimStateUnreadable(ILogger? logger, Exception ex, Guid runId, string stepKey)
    {
        if (logger is not null)
        {
            ClaimStateUnreadableCore(logger, ex, runId, stepKey);
        }
    }

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Error,
        Message = "Signal delivered for run {RunId} step {StepKey}, but the resume nudge could not be dispatched (delayed path: {Delayed}). The step will not wake until its safety-net invocation — 24 hours when no timeoutSeconds is configured.")]
    private static partial void ResumeDispatchFailedCore(ILogger logger, Exception ex, Guid runId, string stepKey, bool delayed);

    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Warning,
        Message = "Could not read claim state for run {RunId} step {StepKey}; falling back to the delayed resume path, which reinstates scheduled-set latency on polling runtimes.")]
    private static partial void ClaimStateUnreadableCore(ILogger logger, Exception ex, Guid runId, string stepKey);
}
