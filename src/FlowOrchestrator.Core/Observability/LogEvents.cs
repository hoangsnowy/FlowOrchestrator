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

    /// <summary>A flow event could not be persisted. Engine continues; log is informational.</summary>
    public static readonly EventId EventPersistenceFailed = new(3002, nameof(EventPersistenceFailed));

    /// <summary>The realtime <c>IFlowEventNotifier</c> threw while publishing. Engine continues; log is informational.</summary>
    public static readonly EventId EventNotifierFailed = new(3003, nameof(EventNotifierFailed));
}
