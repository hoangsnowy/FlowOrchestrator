using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Observability;

/// <summary>
/// Source-generated <see cref="LoggerMessage"/> methods for the signal-delivery path
/// (<c>FlowSignalDispatcher</c>). Mirrors the <see cref="EngineLog"/> pattern: allocation-free,
/// AOT-friendly delegates with stable <see cref="EventId"/> values.
/// </summary>
/// <remarks>
/// Every event here describes a failure that is deliberately swallowed, because the signal itself is
/// already durably persisted before the nudge is attempted and the caller is told the delivery
/// succeeded. Swallowing them silently would be wrong: a lost nudge leaves the step parked until its
/// safety-net invocation, <c>FlowSignalOptions.IndefiniteParkInterval</c> away (5 minutes by default;
/// 24 hours before v1.33) when the step declares no <c>timeoutSeconds</c>.
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
        Message = "Signal delivered for run {RunId} step {StepKey}, but the resume nudge could not be dispatched (delayed path: {Delayed}). The step will not wake until its safety-net invocation (FlowSignalOptions.IndefiniteParkInterval when no timeoutSeconds is configured).")]
    private static partial void ResumeDispatchFailedCore(ILogger logger, Exception ex, Guid runId, string stepKey, bool delayed);

    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Warning,
        Message = "Could not read claim state for run {RunId} step {StepKey}; falling back to the delayed resume path, which reinstates scheduled-set latency on polling runtimes.")]
    private static partial void ClaimStateUnreadableCore(ILogger logger, Exception ex, Guid runId, string stepKey);

    /// <summary>
    /// Logs a resume whose parking invocation still held the execution claim after the whole wait budget.
    /// </summary>
    /// <param name="logger">Logger, or <see langword="null"/> when none was injected.</param>
    /// <param name="runId">Run owning the parked step.</param>
    /// <param name="stepKey">Step whose claim never cleared.</param>
    /// <param name="budget">The wait budget that elapsed.</param>
    /// <remarks>
    /// The delayed nudge is still sent, and the step's bounded safety-net interval recovers it if that
    /// nudge also loses the claim. The warning exists because this condition used to be silent (#190):
    /// the nudge dispatched successfully and then lost the claim inside the worker, leaving nothing in
    /// the logs to distinguish a stranded step from a slow one.
    /// </remarks>
    public static void ResumeClaimStillHeld(ILogger? logger, Guid runId, string stepKey, TimeSpan budget)
    {
        if (logger is not null)
        {
            ResumeClaimStillHeldCore(logger, runId, stepKey, budget);
        }
    }

    [LoggerMessage(
        EventId = 4003,
        Level = LogLevel.Warning,
        Message = "Signal delivered for run {RunId} step {StepKey}, but the parking invocation still held its execution claim after {Budget}; sending a delayed resume nudge. If it also loses the claim, the step wakes at its safety-net invocation.")]
    private static partial void ResumeClaimStillHeldCore(ILogger logger, Guid runId, string stepKey, TimeSpan budget);
}
