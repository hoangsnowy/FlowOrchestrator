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
    /// Appends the runtime keys of every direct child of iteration <paramref name="index"/> —
    /// <c>"{loop}.{index}.{child}"</c> — to <paramref name="keys"/>: the rows
    /// <see cref="IsIterationSettled"/> reads.
    /// </summary>
    /// <param name="scoped">The scope whose body defines the child keys.</param>
    /// <param name="runtimeLoopKey">Runtime key of the scope step.</param>
    /// <param name="index">The zero-based iteration index.</param>
    /// <param name="keys">Receives the keys.</param>
    /// <remarks>
    /// O(|body|) per call, one string per child: used to build the engine's keyed status read for one
    /// iteration, so the cost is independent of how many iterations the loop has.
    /// </remarks>
    public static void AddIterationKeys(IScopedStep scoped, string runtimeLoopKey, int index, List<string> keys)
    {
        var prefix = $"{runtimeLoopKey}.{index}.";
        foreach (var childKey in scoped.Steps.Keys)
        {
            keys.Add(prefix + childKey);
        }
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
    /// <remarks>
    /// For callers that already hold the run's whole ledger and status map (recovery). Since #189
    /// P0-1 the engine's per-step-completion path holds neither: it reads the settled-iteration count
    /// from the store and admits the window <c>[settled, settled + limit)</c> by key
    /// (<c>FlowOrchestratorEngine.AdvanceLoopAsync</c>), which admits exactly what this method would.
    /// </remarks>
    public static IReadOnlyList<LoopAdmissionRequest> NextAdmissions(
        StepMetadata? loopMetadata,
        string runtimeLoopKey,
        int iterations,
        IReadOnlyDictionary<string, StepStatus> statuses,
        IReadOnlySet<string> dispatchedStepKeys)
    {
        if (!TryGetBody(loopMetadata, iterations, out var scoped, out var entries))
        {
            return [];
        }

        var firstPending = FindFirstPending(entries, runtimeLoopKey, 0, iterations, statuses, dispatchedStepKeys);
        return BuildAdmissions(loopMetadata!, scoped, entries, runtimeLoopKey, iterations, firstPending, statuses);
    }

    /// <summary>
    /// Returns the same admissions as <see cref="NextAdmissions"/>, reading the dispatch ledger
    /// through point lookups instead of a pre-fetched set of every dispatched key in the run.
    /// </summary>
    /// <param name="loopMetadata">The scope step's manifest metadata.</param>
    /// <param name="runtimeLoopKey">Runtime key of the scope step.</param>
    /// <param name="iterations">Iteration count the scope recorded in its output.</param>
    /// <param name="statuses">Current runtime status map for the run.</param>
    /// <param name="isDispatched">
    /// Point lookup against the run's dispatch ledger, typically
    /// <see cref="Storage.IFlowRunStore.IsStepDispatchedAsync"/>.
    /// </param>
    /// <returns>The admissions <see cref="NextAdmissions"/> would return for the same ledger state.</returns>
    /// <remarks>
    /// <para>
    /// The engine used to fetch the whole ledger (<c>GetDispatchedStepKeysAsync</c>, one row per
    /// dispatched step) on every pass while a loop was parked: O(n) payload per step completion and
    /// O(n²) over the run (#189). The gate only needs membership for the handful of keys its search
    /// probes.
    /// </para>
    /// <para>
    /// The boundary is found in two phases. The first searches the status map alone — synchronous,
    /// no I/O. Its result <c>b</c> is a lower bound on the true boundary: the search only ever stops
    /// directly above an index it saw with a status row, anything with a status row is started, and
    /// so every index below <c>b</c> is started by the prefix invariant. The second phase gallops
    /// forward from <c>b</c> on the full predicate (status row or ledger row), consulting the ledger
    /// only for indices without a status row. In the sequential steady state — the previous iteration
    /// just settled, nothing dispatched-but-unstarted — <c>b</c> already is the boundary and the pass
    /// costs one ledger lookup per entry child; a window of <c>k</c> dispatched-but-unstarted
    /// iterations costs O(log k) lookups.
    /// </para>
    /// <para>
    /// <b>Complexity per call:</b> O(log n) status-map probes and O(log k) ledger point lookups to find
    /// the boundary, plus the backwards active-count walk shared with <see cref="NextAdmissions"/>,
    /// which is still O(settled iterations) — see the remarks on <see cref="FindFirstPending"/>.
    /// It needs the run's whole status map, so since #189 P0-1 the engine no longer calls it per step
    /// completion (see <see cref="NextAdmissions"/>); it stays as the ledger-by-lookup form of that
    /// reference algorithm.
    /// </para>
    /// </remarks>
    public static async ValueTask<IReadOnlyList<LoopAdmissionRequest>> NextAdmissionsAsync(
        StepMetadata? loopMetadata,
        string runtimeLoopKey,
        int iterations,
        IReadOnlyDictionary<string, StepStatus> statuses,
        Func<string, Task<bool>> isDispatched)
    {
        if (!TryGetBody(loopMetadata, iterations, out var scoped, out var entries))
        {
            return [];
        }

        var statusBoundary = FindFirstPending(entries, runtimeLoopKey, 0, iterations, statuses, dispatchedStepKeys: null);
        var firstPending = await FindFirstPendingAsync(
            entries, runtimeLoopKey, statusBoundary, iterations, statuses, isDispatched).ConfigureAwait(false);

        return BuildAdmissions(loopMetadata!, scoped, entries, runtimeLoopKey, iterations, firstPending, statuses);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="loopMetadata"/> is a scope with at least
    /// one entry child and <paramref name="iterations"/> is positive, yielding the scope and its entries.
    /// </summary>
    private static bool TryGetBody(
        StepMetadata? loopMetadata,
        int iterations,
        out IScopedStep scoped,
        out IReadOnlyList<KeyValuePair<string, StepMetadata>> entries)
    {
        if (iterations <= 0 || loopMetadata is not IScopedStep scope || scope.Steps.Count == 0)
        {
            scoped = null!;
            entries = [];
            return false;
        }

        scoped = scope;
        entries = EntrySteps(scope);
        return entries.Count > 0;
    }

    /// <summary>
    /// Applies the concurrency limit to the un-started block starting at
    /// <paramref name="firstPending"/> and returns the dispatch requests for the iterations it admits.
    /// </summary>
    private static IReadOnlyList<LoopAdmissionRequest> BuildAdmissions(
        StepMetadata loopMetadata,
        IScopedStep scoped,
        IReadOnlyList<KeyValuePair<string, StepMetadata>> entries,
        string runtimeLoopKey,
        int iterations,
        int firstPending,
        IReadOnlyDictionary<string, StepStatus> statuses)
    {
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
    /// <param name="start">An index every lower index of which is known to be started; the search starts there.</param>
    /// <param name="iterations">Iteration count the scope recorded in its output.</param>
    /// <param name="statuses">Current runtime status map for the run.</param>
    /// <param name="dispatchedStepKeys">
    /// Step keys with a live dispatch-ledger row, or <see langword="null"/> to search the status map
    /// alone (the first phase of <see cref="NextAdmissionsAsync"/>).
    /// </param>
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
        int start,
        int iterations,
        IReadOnlyDictionary<string, StepStatus> statuses,
        IReadOnlySet<string>? dispatchedStepKeys)
    {
        if (start >= iterations)
        {
            return iterations;
        }

        if (!IsIterationStarted(entries, runtimeLoopKey, start, statuses, dispatchedStepKeys))
        {
            return start;
        }

        // Gallop: find the first unstarted index by doubling the stride from the known-started start.
        // `low` is always started, `high` is the first index known NOT to be started (or the end).
        var low = start;
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
    /// <see cref="FindFirstPending"/> on the full started predicate, with the ledger read through
    /// <paramref name="isDispatched"/> point lookups instead of a pre-fetched set.
    /// </summary>
    /// <param name="entries">Entry children of the scope.</param>
    /// <param name="runtimeLoopKey">Runtime key of the scope step.</param>
    /// <param name="start">An index every lower index of which is known to be started.</param>
    /// <param name="iterations">Iteration count the scope recorded in its output.</param>
    /// <param name="statuses">Current runtime status map for the run.</param>
    /// <param name="isDispatched">Point lookup against the run's dispatch ledger.</param>
    /// <remarks>
    /// Same gallop + binary search as <see cref="FindFirstPending"/>, with the same result for the
    /// same ledger state. Duplicated rather than shared so the synchronous recovery caller neither
    /// pays for nor blocks on an asynchronous probe.
    /// </remarks>
    private static async ValueTask<int> FindFirstPendingAsync(
        IReadOnlyList<KeyValuePair<string, StepMetadata>> entries,
        string runtimeLoopKey,
        int start,
        int iterations,
        IReadOnlyDictionary<string, StepStatus> statuses,
        Func<string, Task<bool>> isDispatched)
    {
        if (start >= iterations)
        {
            return iterations;
        }

        if (!await IsIterationStartedAsync(entries, runtimeLoopKey, start, statuses, isDispatched).ConfigureAwait(false))
        {
            return start;
        }

        var low = start;
        var stride = 1;
        var high = iterations;
        while (low + stride < iterations)
        {
            var probe = low + stride;
            if (await IsIterationStartedAsync(entries, runtimeLoopKey, probe, statuses, isDispatched).ConfigureAwait(false))
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

        while (high - low > 1)
        {
            var mid = low + ((high - low) >> 1);
            if (await IsIterationStartedAsync(entries, runtimeLoopKey, mid, statuses, isDispatched).ConfigureAwait(false))
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
    /// Asynchronous <see cref="IsIterationStarted"/>: every entry child is checked against the status
    /// map first, and the ledger is consulted only when none of them has a status row.
    /// </summary>
    private static async ValueTask<bool> IsIterationStartedAsync(
        IReadOnlyList<KeyValuePair<string, StepMetadata>> entries,
        string runtimeLoopKey,
        int index,
        IReadOnlyDictionary<string, StepStatus> statuses,
        Func<string, Task<bool>> isDispatched)
    {
        var prefix = $"{runtimeLoopKey}.{index}.";

        // Status pass first, so a status hit on any entry child answers the probe without a ledger
        // round-trip. Indexed loops for the reason given in IsIterationStarted.
        for (var i = 0; i < entries.Count; i++)
        {
            if (statuses.ContainsKey(prefix + entries[i].Key))
            {
                return true;
            }
        }

        for (var i = 0; i < entries.Count; i++)
        {
            if (await isDispatched(prefix + entries[i].Key).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
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
        IReadOnlySet<string>? dispatchedStepKeys)
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
            if (statuses.ContainsKey(key) || (dispatchedStepKeys?.Contains(key) ?? false))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTerminal(StepStatus status) =>
        status is StepStatus.Succeeded or StepStatus.Failed or StepStatus.Skipped;
}
