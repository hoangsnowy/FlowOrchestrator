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

        // All() rather than a foreach whose body is one guard — the CodeQL-preferred shape
        // (cs/linq/missed-where). Short-circuits on the first non-terminal child exactly as the
        // loop did, and this runs once per (iteration, barrier check), not per step.
        return scoped.Steps.Keys.All(childKey =>
            statuses.TryGetValue(prefix + childKey, out var status) && IsTerminal(status));
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

        var firstPending = FindFirstPending(entries, runtimeLoopKey, iterations, statuses, dispatchedStepKeys);

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
    /// Returns the lowest iteration index that has not been handed out yet, or
    /// <paramref name="iterations"/> when every iteration has started.
    /// </summary>
    /// <param name="entries">Entry children of the scope — the steps an admission dispatches.</param>
    /// <param name="runtimeLoopKey">Runtime key of the scope step.</param>
    /// <param name="iterations">Iteration count the scope recorded in its output.</param>
    /// <param name="statuses">Current runtime status map for the run.</param>
    /// <param name="dispatchedStepKeys">Step keys with a live dispatch-ledger row.</param>
    /// <remarks>
    /// Started iterations form a prefix: <see cref="NextAdmissions"/> only ever admits the
    /// contiguous block <c>[firstPending, firstPending + slots)</c>, so an iteration can only have
    /// started if every lower one did. That monotonicity is what makes the boundary findable by
    /// galloping search — double the stride until an unstarted index is found, then binary-search
    /// the last interval — in O(log n) probes.
    /// <para>
    /// The previous implementation walked <c>0..k</c> linearly on every call, which is O(n) per
    /// admission and therefore O(n²) over a loop run.
    /// </para>
    /// <para>
    /// <b>This does not, on its own, make <see cref="NextAdmissions"/> sublinear</b>, and the
    /// measurements say so plainly: at 500 iterations with every one but the last settled,
    /// <c>NextAdmissions</c> costs 87 µs and 174 KB whether the prefix is found by this search or by
    /// the linear walk it replaced. The dominant term is the backwards active-count loop below,
    /// which still visits every settled iteration because it can only stop early once it has seen
    /// <c>ConcurrencyLimit</c> iterations that are <i>not</i> settled — and when they are all
    /// settled it never does. Making that loop sublinear needs settled-state the engine does not
    /// currently track, so it is deferred to
    /// <see href="https://github.com/hoangsnowy/FlowOrchestrator/issues/189">#189</see>.
    /// This search is kept because it is strictly cheaper than the walk it replaced and stops being
    /// masked the moment that loop is fixed.
    /// </para>
    /// </remarks>
    private static int FindFirstPending(
        IReadOnlyList<KeyValuePair<string, StepMetadata>> entries,
        string runtimeLoopKey,
        int iterations,
        IReadOnlyDictionary<string, StepStatus> statuses,
        IReadOnlySet<string> dispatchedStepKeys)
    {
        if (!IsIterationStarted(entries, runtimeLoopKey, 0, statuses, dispatchedStepKeys))
        {
            return 0;
        }

        // Gallop: find the first unstarted index by doubling the stride from the known-started 0.
        // `low` is always started, `high` is the first index known NOT to be started (or the end).
        var low = 0;
        var stride = 1;
        var high = iterations;
        while (low + stride < iterations)
        {
            var probe = low + stride;
            if (IsIterationStarted(entries, runtimeLoopKey, probe, statuses, dispatchedStepKeys))
            {
                low = probe;
                stride <<= 1;
            }
            else
            {
                high = probe;
                break;
            }
        }

        // Binary-search the boundary inside (low, high).
        while (high - low > 1)
        {
            var mid = low + ((high - low) >> 1);
            if (IsIterationStarted(entries, runtimeLoopKey, mid, statuses, dispatchedStepKeys))
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return high;
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
        // Built once per probe rather than once per entry child, and walked with a foreach rather
        // than an Any(lambda): the closure plus two concatenations per child were the bulk of the
        // 544 bytes each probe used to allocate.
        var prefix = $"{runtimeLoopKey}.{index}.";

        // Indexed loop rather than foreach-over-projection: a Select would reintroduce the closure
        // and the per-element allocation this method exists to avoid, and it runs on the engine's
        // per-step-completion path.
        for (var i = 0; i < entries.Count; i++)
        {
            var key = prefix + entries[i].Key;
            if (statuses.ContainsKey(key) || dispatchedStepKeys.Contains(key))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTerminal(StepStatus status) =>
        status is StepStatus.Succeeded or StepStatus.Failed or StepStatus.Skipped;
}
