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
    /// This is the primary guard against duplicate step execution. Since v1.22 the engine calls
    /// this at the top of <c>RunStepAsync</c> (execute time) rather than at schedule time, so it
    /// correctly prevents concurrent execution under at-least-once delivery — including the
    /// Service Bus topic-broadcast case where a single dispatched message reaches multiple
    /// subscriptions. Implementations must use an atomic compare-and-set or equivalent database
    /// primitive. Pair with <see cref="ReleaseStepClaimAsync(Guid, string)"/> on retry / Pending
    /// re-schedule paths so the SAME step can claim again on a fresh attempt.
    /// </remarks>
    Task<bool> TryClaimStepAsync(Guid runId, string stepKey);

    /// <summary>
    /// Releases a previously-acquired step claim so a future <see cref="TryClaimStepAsync"/> for the
    /// same <c>(runId, stepKey)</c> can succeed. Called by the engine on Pending re-schedule and
    /// retry paths, where the same logical step needs to run again.
    /// </summary>
    /// <remarks>
    /// Idempotent — calling for a key with no claim is a no-op. Default implementation is a no-op
    /// so existing custom runtime stores continue to compile; in that case retry/Pending paths
    /// behave as before v1.22 (the schedule-time claim was non-strict). New implementations
    /// should remove the claim row atomically.
    /// </remarks>
    Task ReleaseStepClaimAsync(Guid runId, string stepKey) => Task.CompletedTask;

    /// <summary>
    /// Records a step as <see cref="StepStatus.Skipped"/> without executing it,
    /// used when <c>runAfter</c> conditions cannot be satisfied.
    /// </summary>
    Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason);

    /// <summary>
    /// Records a step as <see cref="StepStatus.Skipped"/> and persists a
    /// <see cref="Expressions.WhenEvaluationTrace"/> describing why a <c>When</c>
    /// clause evaluated to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Default implementation falls back to <see cref="RecordSkippedStepAsync(Guid, string, string, string?)"/>
    /// so existing custom storage providers continue to compile without modification.
    /// </remarks>
    Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason, string? evaluationTraceJson)
        => RecordSkippedStepAsync(runId, stepKey, stepType, reason);

    /// <summary>
    /// Returns the current overall status of the run (<c>"Running"</c>, <c>"Succeeded"</c>, etc.),
    /// or <see langword="null"/> if the run does not exist.
    /// </summary>
    Task<string?> GetRunStatusAsync(Guid runId);
}
