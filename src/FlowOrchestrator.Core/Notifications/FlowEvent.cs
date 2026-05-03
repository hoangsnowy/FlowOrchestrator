namespace FlowOrchestrator.Core.Notifications;

/// <summary>
/// Base type for all flow lifecycle events published through <see cref="IFlowEventNotifier"/>.
/// Subtypes carry a constant <see cref="Type"/> discriminator string used as the SSE
/// <c>event:</c> field name and as a tag for log/metric pipelines.
/// </summary>
public abstract record FlowLifecycleEvent
{
    /// <summary>The event discriminator, e.g. <c>run.started</c>, <c>step.completed</c>.</summary>
    public abstract string Type { get; }

    /// <summary>The flow run this event belongs to.</summary>
    public Guid RunId { get; init; }

    /// <summary>UTC timestamp at which the underlying state change was observed.</summary>
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Emitted once per successful <c>TriggerAsync</c> after the run row is persisted.</summary>
public sealed record RunStartedEvent : FlowLifecycleEvent
{
    /// <inheritdoc/>
    public override string Type => "run.started";

    /// <summary>Identifier of the flow whose execution has started.</summary>
    public Guid FlowId { get; init; }

    /// <summary>Display name of the flow, mirroring <c>FlowDefinition.GetType().Name</c>.</summary>
    public string FlowName { get; init; } = string.Empty;

    /// <summary>The trigger key that initiated the run (e.g. <c>manual</c>, <c>cron</c>, <c>webhook</c>).</summary>
    public string TriggerKey { get; init; } = string.Empty;
}

/// <summary>Emitted whenever a step transitions to a terminal status (Succeeded / Failed / Skipped).</summary>
public sealed record StepCompletedEvent : FlowLifecycleEvent
{
    /// <inheritdoc/>
    public override string Type => "step.completed";

    /// <summary>The step key, unique within the flow manifest.</summary>
    public string StepKey { get; init; } = string.Empty;

    /// <summary>Terminal status name — <c>Succeeded</c>, <c>Failed</c>, <c>Skipped</c>, or <c>Cancelled</c>.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Failure reason when <see cref="Status"/> is <c>Failed</c>; <see langword="null"/> otherwise.</summary>
    public string? FailedReason { get; init; }
}

/// <summary>Emitted when a step is queued for retry via <c>RetryStepAsync</c>.</summary>
public sealed record StepRetriedEvent : FlowLifecycleEvent
{
    /// <inheritdoc/>
    public override string Type => "step.retried";

    /// <summary>The step key being retried.</summary>
    public string StepKey { get; init; } = string.Empty;
}

/// <summary>
/// Emitted when the run reaches a terminal state. The status field captures the
/// outcome — <c>Succeeded</c>, <c>Failed</c>, <c>Skipped</c>, or <c>Cancelled</c>;
/// no separate <c>RunCancelled</c> event exists.
/// </summary>
public sealed record RunCompletedEvent : FlowLifecycleEvent
{
    /// <inheritdoc/>
    public override string Type => "run.completed";

    /// <summary>Terminal run status — same string written by <c>IFlowRunStore.CompleteRunAsync</c>.</summary>
    public string Status { get; init; } = string.Empty;
}
