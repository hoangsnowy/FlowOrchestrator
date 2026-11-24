namespace FlowOrchestrator.Core.Execution;

/// <summary>What happened to a manual retry requested through <see cref="IFlowOrchestrator.RetryStepAsync"/>.</summary>
public enum StepRetryOutcome
{
    /// <summary>The step was reset and its new attempt was handed to the runtime; it runs asynchronously.</summary>
    Accepted,

    /// <summary>The step has no row or is not <c>Failed</c>; only a failed step can be retried. Nothing changed.</summary>
    NotFailed,

    /// <summary>
    /// The step was <c>Failed</c> when the request was checked, but another retry reset it first. That
    /// retry runs the step; this one changed nothing.
    /// </summary>
    Conflict
}

/// <summary>
/// The value <see cref="FlowOrchestratorEngine.RetryStepAsync"/> returns: whether the retry was accepted, and
/// why not when it was not.
/// </summary>
/// <param name="RunId">The run containing the step.</param>
/// <param name="StepKey">The retried step.</param>
/// <param name="Outcome">The outcome.</param>
/// <remarks>
/// A retry is no longer executed on the caller's thread: an accepted retry has only been dispatched when the
/// call returns, and the step's result arrives through the run like any other attempt.
/// </remarks>
public sealed record StepRetryResult(Guid RunId, string StepKey, StepRetryOutcome Outcome)
{
    /// <summary>Whether the step was reset and dispatched.</summary>
    public bool Accepted => Outcome == StepRetryOutcome.Accepted;
}
