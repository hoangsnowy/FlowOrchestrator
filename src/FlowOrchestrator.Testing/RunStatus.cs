namespace FlowOrchestrator.Testing;

/// <summary>
/// Strongly-typed run status mirroring the values stored in <c>FlowRunRecord.Status</c>.
/// Returned on <see cref="FlowTestRunResult.Status"/> so test code can compare without string literals.
/// </summary>
public enum RunStatus
{
    /// <summary>Run is in flight (also returned when the test-host timeout elapses before the run completes).</summary>
    Running = 0,

    /// <summary>Run finished and every step finished with <see cref="FlowOrchestrator.Core.Abstractions.StepStatus.Succeeded"/> or <see cref="FlowOrchestrator.Core.Abstractions.StepStatus.Skipped"/>.</summary>
    Succeeded = 1,

    /// <summary>Run finished with at least one step in <see cref="FlowOrchestrator.Core.Abstractions.StepStatus.Failed"/> and no fallback path.</summary>
    Failed = 2,

    /// <summary>Run was cancelled before reaching a natural terminal state.</summary>
    Cancelled = 3,

    /// <summary>Run was terminated by the configured run timeout (distinct from the test-host's <c>timeout</c> parameter).</summary>
    TimedOut = 4
}
