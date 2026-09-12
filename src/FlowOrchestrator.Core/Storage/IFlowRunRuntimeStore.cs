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
    /// Returns whether one specific step currently holds an execution claim.
    /// </summary>
    /// <param name="runId">The run owning the step.</param>
    /// <param name="stepKey">The step to test.</param>
    /// <returns><see langword="true"/> when a claim row exists for <c>(runId, stepKey)</c>.</returns>
    /// <remarks>
    /// A point lookup for callers that need membership rather than enumeration — notably
    /// <c>FlowSignalDispatcher</c>, which asks about a single parked step on every signal delivery.
    /// Claims are deliberately NOT released when a step reaches a terminal status (the row is what
    /// makes execution exactly-once under at-least-once delivery), so <see cref="GetClaimedStepKeysAsync"/>
    /// returns one key per executed step and grows with run length; answering a single-key question
    /// through it is O(n) in both payload and scan. The default implementation does exactly that, so
    /// existing stores keep compiling — override it with an indexed lookup.
    /// </remarks>
    async Task<bool> IsStepClaimedAsync(Guid runId, string stepKey)
    {
        var claimed = await GetClaimedStepKeysAsync(runId).ConfigureAwait(false);
        return claimed.Contains(stepKey, StringComparer.Ordinal);
    }

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
