using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Runtime step-tracking store used by the orchestrator during execution to prevent
/// duplicate step enqueuing in parallel/fan-out scenarios.
/// </summary>
public interface IFlowRunRuntimeStore
{
    /// <summary>Returns the current <see cref="StepStatus"/> for every step in the run.</summary>
    Task<IReadOnlyDictionary<string, StepStatus>> GetStepStatusesAsync(Guid runId);

    /// <summary>
    /// Returns the set of step keys that have been claimed (locked) for execution
    /// but not yet completed, used to detect in-progress steps.
    /// </summary>
    Task<IReadOnlyCollection<string>> GetClaimedStepKeysAsync(Guid runId);

    /// <summary>
    /// Atomically claims a step for execution, returning <see langword="true"/> if this caller
    /// acquired the claim or <see langword="false"/> if another worker already claimed it.
    /// </summary>
    /// <remarks>
    /// This is the primary guard against duplicate step execution in a multi-worker Hangfire setup.
    /// Implementations must use an atomic compare-and-set or equivalent database primitive.
    /// </remarks>
    Task<bool> TryClaimStepAsync(Guid runId, string stepKey);

    /// <summary>
    /// Records a step as <see cref="StepStatus.Skipped"/> without executing it,
    /// used when <c>runAfter</c> conditions cannot be satisfied.
    /// </summary>
    Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason);

    /// <summary>
    /// Returns the current overall status of the run (<c>"Running"</c>, <c>"Succeeded"</c>, etc.),
    /// or <see langword="null"/> if the run does not exist.
    /// </summary>
    Task<string?> GetRunStatusAsync(Guid runId);
}
