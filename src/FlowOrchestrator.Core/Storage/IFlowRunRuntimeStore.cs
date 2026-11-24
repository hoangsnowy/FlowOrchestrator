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
    /// Atomically deletes every claim of <paramref name="runId"/> that has no step status row and was taken
    /// at least <paramref name="minimumAge"/> ago, together with the same keys' dispatch-ledger rows, and
    /// returns the keys it released.
    /// </summary>
    /// <param name="runId">The run whose orphaned claims are released.</param>
    /// <param name="minimumAge">
    /// How old a claim must be, measured on the store's own clock (the one that stamped the claim), before
    /// it counts as orphaned. <see cref="TimeSpan.Zero"/> releases every claim without a status row.
    /// </param>
    /// <returns>The released step keys; empty when nothing was orphaned.</returns>
    /// <remarks>
    /// <para>
    /// A worker records the step's first status row right after it wins the claim. A claim with no row
    /// therefore means that worker crashed (or its storage failed) in between, and nothing else would ever
    /// run that step: the claim makes every later attempt exit, and the completion gate counts the key as
    /// in-flight work for ever (RFC 2.0 S1b). Releasing the claim and the ledger row returns the step to
    /// "ready, never dispatched", which recovery re-dispatches.
    /// </para>
    /// <para>
    /// The age is the only evidence that the claimer is gone, so a worker that is alive but stalled for
    /// longer than <paramref name="minimumAge"/> between its claim and its first status write can have its
    /// claim released and the step run twice. The lease redesign in RFC 2.0 (P3) replaces this with an
    /// owned, renewed claim. There is deliberately no default implementation.
    /// </para>
    /// <para>
    /// The check for a status row must serialise with a concurrent first status write for the same key
    /// (a key-range lock or one statement), so a claim whose row is being written is never released.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<string>> ReleaseOrphanedClaimsAsync(Guid runId, TimeSpan minimumAge);

    /// <summary>
    /// Records a step as <see cref="StepStatus.Skipped"/> without executing it,
    /// used when <c>runAfter</c> conditions cannot be satisfied.
    /// </summary>
    /// <remarks>
    /// The engine calls this right after it wins <see cref="TryClaimStepAsync"/>, so the step row and its
    /// attempt row should be written in one transaction: a skip that is half written leaves a claim the
    /// completion gate cannot tell apart from a step that is still starting.
    /// </remarks>
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
