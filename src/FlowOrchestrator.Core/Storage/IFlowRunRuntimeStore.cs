using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Runtime step-tracking store used by the orchestrator during execution to prevent
/// duplicate step enqueuing in parallel/fan-out scenarios.
/// </summary>
public interface IFlowRunRuntimeStore
{
    /// <summary>Returns the current <see cref="StepStatus"/> for every step in the run.</summary>
    /// <remarks>
    /// O(steps in the run) in rows returned. The engine calls it only when a run is about to close (the
    /// terminal classification) and from startup recovery — never once per step completion; that path uses
    /// <see cref="GetStepStatusesAsync(Guid, IReadOnlyCollection{string})"/>.
    /// </remarks>
    Task<IReadOnlyDictionary<string, StepStatus>> GetStepStatusesAsync(Guid runId);

    /// <summary>
    /// Returns the current <see cref="StepStatus"/> of each of <paramref name="stepKeys"/> that has a status
    /// row; keys without one are absent from the result.
    /// </summary>
    /// <param name="runId">The run owning the steps.</param>
    /// <param name="stepKeys">Runtime step keys to look up. May contain duplicates; may be empty.</param>
    /// <returns>A map keyed with <see cref="StringComparer.Ordinal"/> holding only keys that have a row.</returns>
    /// <remarks>
    /// <para>
    /// The engine's per-step-completion read (#189 P0-1). When step <c>X</c> reaches a status, only the steps
    /// that depend on <c>X</c> can change classification, so the continuation asks for those steps and their
    /// dependencies — a handful of keys — instead of every row in the run. Implement it as indexed point
    /// lookups on <c>(RunId, StepKey)</c>; a provider with a statement parameter limit must chunk.
    /// </para>
    /// <para>
    /// <b>Visibility contract (write-skew rule, RFC 2.0 §3.5).</b> The call must observe every status write
    /// that committed before it started. Run it as its own statement against the primary — never inside a
    /// transaction that predates the caller's write, never against a read replica. Two steps that complete
    /// concurrently then cannot both miss the join that waits on them: whichever commits last reads the
    /// other's row.
    /// </para>
    /// <para>There is deliberately no default implementation: falling back to the whole map would silently keep
    /// the continuation O(steps in the run).</para>
    /// </remarks>
    Task<IReadOnlyDictionary<string, StepStatus>> GetStepStatusesAsync(Guid runId, IReadOnlyCollection<string> stepKeys);

    /// <summary>
    /// Returns whether any step of the run currently has a <see cref="StepStatus.Running"/> or
    /// <see cref="StepStatus.Pending"/> status row.
    /// </summary>
    /// <param name="runId">The run to inspect.</param>
    /// <returns><see langword="true"/> when at least one such row exists.</returns>
    /// <remarks>
    /// <para>
    /// The first check of the run-completion gate, asked on every step completion that dispatched nothing.
    /// A Running or Pending row is work that will run a continuation of its own later (a handler still
    /// executing, a ForEach parked on its barrier, a poll waiting for its next attempt), so the gate can stand
    /// down without reading the run. Only when this returns <see langword="false"/> does the engine read the
    /// whole status map, claims and dispatch ledger to decide whether to close the run.
    /// </para>
    /// <para>
    /// Back it with an index on the open rows only (a filtered or partial index on
    /// <c>Status IN ('Running','Pending')</c>), so the cost follows the number of open steps, not the run's
    /// length. The same visibility contract as
    /// <see cref="GetStepStatusesAsync(Guid, IReadOnlyCollection{string})"/> applies. A
    /// <see langword="false"/> that should have been <see langword="true"/> only costs the full read; a
    /// <see langword="true"/> that should have been <see langword="false"/> keeps a finished run open, so
    /// never answer from a cache.
    /// </para>
    /// </remarks>
    Task<bool> HasRunningOrPendingStepAsync(Guid runId);

    /// <summary>
    /// Counts the iterations <c>i</c> in <c>[0, <paramref name="iterations"/>)</c> of the loop instance
    /// <paramref name="loopStepKey"/> in which every one of <paramref name="bodyStepKeys"/> —
    /// <c>"{loopStepKey}.{i}.{child}"</c> — has a terminal status (<see cref="StepStatus.Succeeded"/>,
    /// <see cref="StepStatus.Failed"/> or <see cref="StepStatus.Skipped"/>).
    /// </summary>
    /// <param name="runId">The run owning the loop.</param>
    /// <param name="loopStepKey">Runtime key of the loop step, e.g. <c>"loop"</c> or <c>"outer.2.inner"</c>.</param>
    /// <param name="iterations">The loop's iteration count; indices at or above it are not counted.</param>
    /// <param name="bodyStepKeys">The loop body's direct child keys (single segments, no dots), each listed once.</param>
    /// <returns>The number of settled iterations.</returns>
    /// <remarks>
    /// <para>
    /// Drives the ForEach barrier and admission gate (#189 P0-1): the loop settles when the count reaches
    /// <paramref name="iterations"/>, and admission keeps <c>[0, count + ConcurrencyLimit)</c> started. Only
    /// a row whose key is exactly <c>{loop}.{i}.{child}</c>, with <c>i</c> a canonical decimal index and
    /// <c>child</c> one of <paramref name="bodyStepKeys"/>, counts — rows of nested loops
    /// (<c>{loop}.{i}.{child}.{j}.{grandchild}</c>) do not.
    /// </para>
    /// <para>
    /// The answer travels as one row. Server-side it is a range read over the loop's own rows, so implement it
    /// against the <c>(RunId, StepKey)</c> key with a prefix predicate. The same visibility contract as
    /// <see cref="GetStepStatusesAsync(Guid, IReadOnlyCollection{string})"/> applies; it is what makes two
    /// iterations that settle concurrently admit both replacements.
    /// </para>
    /// </remarks>
    Task<int> CountSettledIterationsAsync(Guid runId, string loopStepKey, int iterations, IReadOnlyCollection<string> bodyStepKeys);

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
    /// Atomically claims a step for execution under a lease that expires after <paramref name="duration"/>
    /// unless renewed, taking over a lease that already expired while the step had not reached a terminal
    /// status.
    /// </summary>
    /// <param name="runId">The run owning the step.</param>
    /// <param name="stepKey">The step to claim.</param>
    /// <param name="ownerId">Identifies the acquiring process, for diagnostics.</param>
    /// <param name="duration">How long the lease lives without renewal, measured on the store's own clock.</param>
    /// <returns>
    /// The acquired lease; <see langword="null"/> when another worker holds an unexpired lease, the claim is
    /// permanent (a skip claim, a lease pinned by <see cref="PinStepLeaseAsync"/>, or a claim written before
    /// leases existed), or the step already reached a terminal status.
    /// </returns>
    /// <remarks>
    /// <para>
    /// No claim row: one is inserted with fence <c>1</c>. A row whose lease expired, for a step with no status
    /// row or a <see cref="StepStatus.Running"/> / <see cref="StepStatus.Pending"/> one: the fence is raised by
    /// one and the lease re-stamped — a takeover. Anything else: <see langword="null"/>.
    /// </para>
    /// <para>
    /// The status check must serialise with a concurrent fenced completion
    /// (<see cref="TryRecordLeasedStepCompleteAsync"/>) of the same step: lock the claim row first, then read
    /// the status, so a completion that committed first is always seen and a takeover never re-runs a step
    /// that just finished.
    /// </para>
    /// </remarks>
    Task<StepLease?> TryAcquireStepLeaseAsync(Guid runId, string stepKey, string ownerId, TimeSpan duration);

    /// <summary>
    /// Extends every lease in <paramref name="leases"/> whose fence still matches by <paramref name="duration"/>
    /// from now, in one round trip.
    /// </summary>
    /// <param name="leases">The leases this process holds for steps that are executing right now.</param>
    /// <param name="duration">The new remaining lifetime, measured on the store's own clock.</param>
    /// <returns>The leases that were renewed; a lease missing from the result was taken over or released.</returns>
    Task<IReadOnlyList<StepLease>> RenewStepLeasesAsync(IReadOnlyList<StepLease> leases, TimeSpan duration);

    /// <summary>
    /// Deletes the claim when its fence still matches <paramref name="lease"/>, so the same step can be claimed
    /// again — the fenced form of <see cref="ReleaseStepClaimAsync"/>, used by the Pending re-poll hand-off.
    /// </summary>
    /// <param name="lease">The lease to release.</param>
    /// <returns><see langword="true"/> when the claim was deleted; <see langword="false"/> when it was taken over.</returns>
    Task<bool> ReleaseStepLeaseAsync(StepLease lease);

    /// <summary>
    /// Makes the lease permanent when its fence still matches: it no longer expires and can no longer be taken
    /// over. Used when a step parks on its loop barrier — its handler has returned, nothing renews the lease any
    /// more, and re-running it would fan the loop out again.
    /// </summary>
    /// <param name="lease">The lease to pin.</param>
    /// <returns><see langword="true"/> when pinned; <see langword="false"/> when it was taken over.</returns>
    Task<bool> PinStepLeaseAsync(StepLease lease);

    /// <summary>
    /// Records a step's completion only while <paramref name="lease"/> is still the step's current lease, in
    /// the same transaction as the fence check.
    /// </summary>
    /// <param name="lease">The lease the completing attempt holds.</param>
    /// <param name="status">The step status to record.</param>
    /// <param name="outputJson">Serialised step output, if any.</param>
    /// <param name="errorMessage">Failure reason, if any.</param>
    /// <returns>
    /// <see langword="true"/> when recorded; <see langword="false"/> when another worker took the lease over —
    /// that worker's attempt owns the step now, and this completion must be discarded, continuation included.
    /// </returns>
    /// <remarks>Same writes as <see cref="IFlowRunStore.RecordStepCompleteAsync"/> (step row and latest attempt row).</remarks>
    Task<bool> TryRecordLeasedStepCompleteAsync(StepLease lease, string status, string? outputJson, string? errorMessage);

    /// <summary>
    /// Selects up to <paramref name="maxCount"/> steps of running runs whose execution lease expired while the
    /// step had not reached a terminal status, and stamps each so that no other caller selects it again within
    /// <paramref name="redeliveryBackoff"/>.
    /// </summary>
    /// <param name="redeliveryBackoff">Minimum time between two redeliveries of the same expired lease.</param>
    /// <param name="maxCount">Upper bound on the rows returned.</param>
    /// <returns>The steps to hand to the runtime again; a redelivery takes the expired lease over.</returns>
    /// <remarks>
    /// Covers the owner dying mid-handler, between its claim and its first status write, and between the handler
    /// returning and the completion committing — every case where the runtime's own redelivery came too early
    /// (the lease was still live) or never came. Ages are measured on the store's own clock. Concurrent callers
    /// on other replicas must not select the same row in the same window.
    /// </remarks>
    Task<IReadOnlyList<ExpiredStepLease>> LeaseExpiredStepLeasesAsync(TimeSpan redeliveryBackoff, int maxCount);

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
    /// claim released and the step run twice. A claim taken under a lease
    /// (<see cref="TryAcquireStepLeaseAsync"/>) is reclaimed by its expiry instead; this release still covers
    /// claims without one. There is deliberately no default implementation.
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
