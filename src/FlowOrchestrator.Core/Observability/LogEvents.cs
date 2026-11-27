using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Observability;

/// <summary>
/// Stable <see cref="EventId"/> constants for the structured-logging story.
/// </summary>
/// <remarks>
/// Production users can filter or alert on a specific event by ID without parsing message templates.
/// IDs are grouped by domain — 1000 series for run lifecycle, 2000 series for step lifecycle,
/// 3000 series for dispatch / runtime, 4000 series for storage / migration. Once published, an ID
/// must keep its meaning across releases.
/// </remarks>
public static class LogEvents
{
    /// <summary>A new run started via <c>TriggerAsync</c>.</summary>
    public static readonly EventId RunStarted = new(1000, nameof(RunStarted));

    /// <summary>A run reached a terminal status.</summary>
    public static readonly EventId RunCompleted = new(1001, nameof(RunCompleted));

    /// <summary>A run failed and was not recovered.</summary>
    public static readonly EventId RunFailed = new(1002, nameof(RunFailed));

    /// <summary>A run start could not be persisted to the run store. Engine continues; log is informational.</summary>
    public static readonly EventId RunStartTrackingFailed = new(1003, nameof(RunStartTrackingFailed));

    /// <summary>
    /// A trigger failed before dispatching any step and the compensation that frees its idempotency key
    /// (cancel and fail the dead run, release the key) failed too.
    /// </summary>
    public static readonly EventId IdempotencyKeyReleaseFailed = new(1011, nameof(IdempotencyKeyReleaseFailed));

    /// <summary>
    /// A scheduled trigger named a key the flow's manifest does not declare as a cron trigger (an orphaned
    /// recurring job from a renamed or removed trigger); no run was started.
    /// </summary>
    public static readonly EventId StaleScheduleRejected = new(1012, nameof(StaleScheduleRejected));

    /// <summary>
    /// A duplicate trigger took over an idempotency key whose registration was older than
    /// <c>FlowRunControlOptions.IdempotencyReservationTimeout</c> and whose run never started work (the
    /// trigger that registered it died before creating the run); the retry starts a new run.
    /// </summary>
    public static readonly EventId IdempotencyReservationTakenOver = new(1013, nameof(IdempotencyReservationTakenOver));

    /// <summary>
    /// After taking over a stale idempotency reservation, completing the abandoned run as <c>Failed</c>
    /// failed; the run's cancel latch is already set, so none of its steps can execute.
    /// </summary>
    public static readonly EventId StaleReservationRunCompletionFailed = new(1014, nameof(StaleReservationRunCompletionFailed));

    /// <summary>A step transitioned to <c>Running</c>.</summary>
    public static readonly EventId StepStarted = new(2000, nameof(StepStarted));

    /// <summary>A step completed without error.</summary>
    public static readonly EventId StepCompleted = new(2001, nameof(StepCompleted));

    /// <summary>A step handler threw or returned a failed result.</summary>
    public static readonly EventId StepFailed = new(2002, nameof(StepFailed));

    /// <summary>A step was skipped because of a false <c>When</c> clause or unmet <c>RunAfter</c>.</summary>
    public static readonly EventId StepSkipped = new(2003, nameof(StepSkipped));

    /// <summary>A step returned <c>Pending</c> and was rescheduled (poll loop).</summary>
    public static readonly EventId StepPending = new(2004, nameof(StepPending));

    /// <summary>A <c>When</c>-clause expression failed to evaluate.</summary>
    public static readonly EventId WhenEvaluationFailed = new(2005, nameof(WhenEvaluationFailed));

    /// <summary>A step start could not be tracked. Engine continues; log is informational.</summary>
    public static readonly EventId StepStartTrackingFailed = new(2006, nameof(StepStartTrackingFailed));

    /// <summary>A step completion could not be tracked. Engine continues; log is informational.</summary>
    public static readonly EventId StepCompletionTrackingFailed = new(2007, nameof(StepCompletionTrackingFailed));

    /// <summary>A skipped-step record could not be persisted. Engine continues; log is informational.</summary>
    public static readonly EventId StepSkipTrackingFailed = new(2008, nameof(StepSkipTrackingFailed));

    /// <summary>A step was enqueued onto the runtime adapter (Hangfire, channel, queue).</summary>
    public static readonly EventId DispatchEnqueued = new(3000, nameof(DispatchEnqueued));

    /// <summary>A best-effort dispatch annotation failed. Engine continues; log is informational.</summary>
    public static readonly EventId DispatchAnnotateFailed = new(3001, nameof(DispatchAnnotateFailed));

    // 3002 (EventPersistenceFailed) is retired: persisted run events were removed in 2.0.
    // Do not reuse the number.

    /// <summary>The realtime <c>IFlowEventNotifier</c> threw while publishing. Engine continues; log is informational.</summary>
    public static readonly EventId EventNotifierFailed = new(3003, nameof(EventNotifierFailed));

    /// <summary>
    /// Re-asserting the dispatch ledger after a failed pending reschedule itself failed. The
    /// original reschedule exception still propagates; this log records the best-effort fallback miss.
    /// </summary>
    public static readonly EventId DispatchReassertFailed = new(3004, nameof(DispatchReassertFailed));

    /// <summary>A loop iteration was admitted and its entry step enqueued.</summary>
    public static readonly EventId LoopIterationAdmitted = new(3006, nameof(LoopIterationAdmitted));

    /// <summary>
    /// A <c>Pending</c> reschedule found the step's dispatch-ledger row already recorded by another
    /// party (recovery, a retry) and dispatched the next attempt anyway; the execution claim absorbs
    /// the duplicate. Logged because a collision here used to be able to strand the step (#190, race 2).
    /// </summary>
    public static readonly EventId PendingRescheduleLedgerTaken = new(3007, nameof(PendingRescheduleLedgerTaken));

    /// <summary>
    /// A parked step found its signal already delivered after releasing its claim and enqueued its own
    /// resume — the delivery whose nudge could only have lost that claim (#190).
    /// </summary>
    public static readonly EventId ResumedSignalDeliveredWhileClaimed = new(3008, nameof(ResumedSignalDeliveredWhileClaimed));

    /// <summary>
    /// The post-release signal-waiter re-check failed; a signal delivered while the step held its claim
    /// now waits for the safety-net invocation.
    /// </summary>
    public static readonly EventId SignalRecheckFailed = new(3009, nameof(SignalRecheckFailed));

    /// <summary>
    /// The Pending path's ledger hand-off threw while the step was still claimed, and the best-effort
    /// claim release that follows failed too; the step stays claimed until the run timeout sweep.
    /// </summary>
    public static readonly EventId ClaimReleaseAfterFailedHandOffFailed = new(3010, nameof(ClaimReleaseAfterFailedHandOffFailed));

    /// <summary>
    /// A step's start (run-control read or start write) threw after it was claimed, and the best-effort
    /// release of its claim and dispatch-ledger row failed too; recovery releases the orphaned claim later.
    /// </summary>
    public static readonly EventId ReleaseAfterFailedStartFailed = new(3012, nameof(ReleaseAfterFailedStartFailed));

    /// <summary>
    /// Recovery or the timeout sweep released claims that had no step status row and were older than
    /// <c>FlowRunControlOptions.OrphanedClaimTimeout</c> (a worker died between claim and start).
    /// </summary>
    public static readonly EventId OrphanedClaimsReleased = new(3013, nameof(OrphanedClaimsReleased));

    /// <summary>
    /// A manual retry was accepted but its dispatch threw, and moving the step back to <c>Failed</c> failed
    /// too; the step stays <c>Running</c> with nothing queued.
    /// </summary>
    public static readonly EventId RetryRestoreFailed = new(3014, nameof(RetryRestoreFailed));

    /// <summary>
    /// A graph continuation went round more passes than the manifest can need without converging, and
    /// left the run open rather than complete it while steps were still ready or owed a skip row.
    /// </summary>
    public static readonly EventId ContinuationPassLimitReached = new(3015, nameof(ContinuationPassLimitReached));

    /// <summary>
    /// A delivery took over a step whose execution lease had expired without a terminal status: the worker
    /// that held it died or lost its store, and the step runs again.
    /// </summary>
    public static readonly EventId StepLeaseTakenOver = new(3030, nameof(StepLeaseTakenOver));

    /// <summary>An attempt found that another worker had taken its expired lease over; its outcome was discarded.</summary>
    public static readonly EventId StepLeaseLost = new(3031, nameof(StepLeaseLost));

    /// <summary>The lease of a loop step parked on its barrier could not be made permanent.</summary>
    public static readonly EventId StepLeasePinFailed = new(3032, nameof(StepLeasePinFailed));
}
