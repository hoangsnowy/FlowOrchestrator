using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution.Internal;

/// <summary>
/// One loop-body entry step of one iteration that the admission gate has cleared for dispatch.
/// </summary>
/// <param name="Index">The zero-based iteration index being admitted.</param>
/// <param name="RuntimeStepKey">The child's runtime key, <c>"{loop}.{index}.{child}"</c>.</param>
/// <param name="Metadata">The child's template metadata, used for its type and inputs.</param>
internal readonly record struct LoopAdmissionRequest(int Index, string RuntimeStepKey, StepMetadata Metadata);

/// <summary>
/// Admission gate that turns <see cref="LoopStepMetadata.ConcurrencyLimit"/> into a real bound on
/// how many iterations of a <c>ForEach</c> body may be in flight at once.
/// </summary>
/// <remarks>
/// <para>
/// Before this gate existed the limit was implemented as a scheduling <i>delay</i>: every iteration
/// was dispatched up front and bucket <c>n</c> was pushed back by <c>n × 100 ms</c>. That throttles
/// nothing — with a body that parks (<c>WaitForSignal</c>, a polling step) every iteration was live
/// 100 ms later, so <c>ConcurrencyLimit = 1</c> still ran the whole body concurrently (issue #181).
/// </para>
/// <para>
/// The gate is derived state, not a stored cursor: an iteration counts as <b>started</b> when one of
/// its entry children has a status row or a live dispatch-ledger row, and as <b>settled</b> when
/// every child of the iteration reached a terminal status. Admitting is therefore idempotent — two
/// workers evaluating the gate concurrently produce the same candidate set, and the dispatch ledger
/// (<see cref="Storage.IFlowRunStore.TryRecordDispatchAsync"/>) makes the duplicate dispatch a no-op.
/// Both signals are needed: the ledger covers an iteration dispatched but not yet picked up by a
/// worker, and the status map covers a step whose ledger row was released for a
/// <see cref="StepStatus.Pending"/> re-poll.
/// </para>
/// <para>
/// Started iterations always form a prefix of <c>0..iterations-1</c>, because admission only ever
/// hands out the lowest un-started indices. The scan relies on that: it walks forward to the first
/// un-started index, then counts active iterations backwards and stops as soon as the limit is
/// reached, so the sequential default costs one probe per pass rather than a full re-walk.
/// </para>
/// </remarks>
internal static class LoopAdmission
{
    /// <summary>Sentinel limit for a scope kind that declares no concurrency bound.</summary>
    public const int Unbounded = int.MaxValue;

    /// <summary>
    /// Returns the loop body's entry steps — those declaring no <c>RunAfter</c> — falling back to
    /// the first declared child when every child is chained, so a body authored as a pure sequence
    /// still has a head to start from.
    /// </summary>
    /// <param name="scoped">The scope whose body is being started.</param>
    public static IReadOnlyList<KeyValuePair<string, StepMetadata>> EntrySteps(IScopedStep scoped)
    {
        var entries = scoped.Steps
            .Where(kvp => kvp.Value.RunAfter.Count == 0)
            .ToList();

        if (entries.Count == 0 && scoped.Steps.Count > 0)
        {
            entries.Add(scoped.Steps.First());
        }

        return entries;
    }

    /// <summary>
    /// Returns the number of iterations of <paramref name="metadata"/> that may run at once, or
    /// <see cref="Unbounded"/> for a scope kind that declares no limit.
    /// </summary>
    /// <param name="metadata">The scope step's manifest metadata.</param>
    public static int ConcurrencyLimitOf(StepMetadata? metadata)
        => metadata is LoopStepMetadata loop ? Math.Max(1, loop.ConcurrencyLimit) : Unbounded;

    /// <summary>
    /// Returns <see langword="true"/> when every child of iteration <paramref name="index"/> has a
    /// terminal status — the same per-iteration test the completion barrier applies.
    /// </summary>
    /// <param name="scoped">The scope whose body defines the child keys.</param>
    /// <param name="runtimeLoopKey">Runtime key of the scope step.</param>
    /// <param name="index">The zero-based iteration index.</param>
    /// <param name="statuses">Current runtime status map for the run.</param>
    public static bool IsIterationSettled(
        IScopedStep scoped,
        string runtimeLoopKey,
        int index,
        IReadOnlyDictionary<string, StepStatus> statuses)
    {
        var prefix = $"{runtimeLoopKey}.{index}.";
        foreach (var childKey in scoped.Steps.Keys)
        {
            if (!statuses.TryGetValue(prefix + childKey, out var status) || !IsTerminal(status))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns the entry-step dispatches for the iterations that may start now, given the loop's
    /// concurrency limit and how many of its iterations are already in flight.
    /// </summary>
    /// <param name="loopMetadata">The scope step's manifest metadata.</param>
    /// <param name="runtimeLoopKey">Runtime key of the scope step, e.g. <c>"polish_process"</c>.</param>
    /// <param name="iterations">Iteration count the scope recorded in its output.</param>
    /// <param name="statuses">Current runtime status map for the run.</param>
    /// <param name="dispatchedStepKeys">Step keys with a live dispatch-ledger row for the run.</param>
    /// <returns>
    /// One request per (iteration, entry child) pair to dispatch, in ascending iteration order;
    /// empty when the limit is saturated or every iteration has already started.
    /// </returns>
    public static IReadOnlyList<LoopAdmissionRequest> NextAdmissions(
        StepMetadata? loopMetadata,
        string runtimeLoopKey,
        int iterations,
        IReadOnlyDictionary<string, StepStatus> statuses,
        IReadOnlySet<string> dispatchedStepKeys)
    {
        if (iterations <= 0 || loopMetadata is not IScopedStep scoped || scoped.Steps.Count == 0)
        {
            return [];
        }

        var entries = EntrySteps(scoped);
        if (entries.Count == 0)
        {
            return [];
        }

        var firstPending = 0;
        while (firstPending < iterations
               && IsIterationStarted(entries, runtimeLoopKey, firstPending, statuses, dispatchedStepKeys))
        {
            firstPending++;
        }

        if (firstPending >= iterations)
        {
            return [];
        }

        var limit = ConcurrencyLimitOf(loopMetadata);
        var slots = iterations - firstPending;

        if (limit != Unbounded)
        {
            var active = 0;
            for (var index = firstPending - 1; index >= 0; index--)
            {
                if (IsIterationSettled(scoped, runtimeLoopKey, index, statuses))
                {
                    continue;
                }

                if (++active >= limit)
                {
                    return [];
                }
            }

            slots = Math.Min(slots, limit - active);
        }

        var admissions = new List<LoopAdmissionRequest>(slots * entries.Count);
        for (var index = firstPending; index < firstPending + slots; index++)
        {
            foreach (var (childKey, childMetadata) in entries)
            {
                admissions.Add(new LoopAdmissionRequest(
                    index,
                    $"{runtimeLoopKey}.{index}.{childKey}",
                    childMetadata));
            }
        }

        return admissions;
    }

    /// <summary>
    /// Returns <see langword="true"/> when iteration <paramref name="index"/> has already been
    /// handed out — an entry child either carries a status row or holds a dispatch-ledger row.
    /// </summary>
    private static bool IsIterationStarted(
        IReadOnlyList<KeyValuePair<string, StepMetadata>> entries,
        string runtimeLoopKey,
        int index,
        IReadOnlyDictionary<string, StepStatus> statuses,
        IReadOnlySet<string> dispatchedStepKeys)
    {
        var prefix = $"{runtimeLoopKey}.{index}.";
        foreach (var (childKey, _) in entries)
        {
            var runtimeChildKey = prefix + childKey;
            if (statuses.ContainsKey(runtimeChildKey) || dispatchedStepKeys.Contains(runtimeChildKey))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTerminal(StepStatus status) =>
        status is StepStatus.Succeeded or StepStatus.Failed or StepStatus.Skipped;
}
