using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution.Internal;

/// <summary>
/// Computes the terminal status of a flow run from its step statuses, applying the
/// same rules the engine applies inline at the end of <c>RunGraphContinuationAsync</c>.
/// Single source of truth — both the engine and <c>FlowRunRecoveryHostedService</c>
/// call into here so a behavioural fix lands in one place.
/// </summary>
internal static class RunTerminationClassifier
{
    /// <summary>
    /// Returns the terminal status string (<see cref="StepStatus.Succeeded"/>,
    /// <see cref="StepStatus.Failed"/>, or <see cref="StepStatus.Skipped"/>) for a run
    /// whose step statuses are all final.
    /// </summary>
    /// <param name="flow">The flow whose manifest provides RunAfter dependencies for leaf detection.</param>
    /// <param name="statuses">The terminal-status map keyed by runtime step key.</param>
    /// <remarks>
    /// Leaf and failure-handling detection operate on runtime step keys (e.g. <c>loop.0.child</c>):
    /// a dependent's template <c>RunAfter</c> keys are resolved into runtime space and a scoped step
    /// is treated as a non-leaf whenever its iteration children are present, so loop bodies classify
    /// from their children rather than from the loop step's own status.
    /// <para>
    /// <b>Complexity: O(n · d)</b> for n statuses and d = the largest <c>RunAfter</c> fan-in, plus
    /// the key lengths for the ancestor walk. Runs once per run completion and once per recovered
    /// run. The previous form asked "is anything depending on this key?" by scanning every other
    /// status for every status, building two strings per comparison — O(n²) time and allocation,
    /// 44.7 ms / 35 MB at a 500-iteration loop (#189). This form inverts the question: one pass
    /// collects every key something depends on, then each status is a set lookup. Keys are held as
    /// <see cref="StepKeySlice"/> views, so no runtime key string is built to be hashed.
    /// </para>
    /// <para>
    /// Rules, in order:
    /// <list type="number">
    ///   <item>No success → <see cref="StepStatus.Failed"/> if any failed, otherwise <see cref="StepStatus.Skipped"/> if any skipped, otherwise <see cref="StepStatus.Failed"/>.</item>
    ///   <item>Any failed step without a downstream successful recovery handler → <see cref="StepStatus.Failed"/>.</item>
    ///   <item>All leaf steps Skipped → <see cref="StepStatus.Skipped"/>.</item>
    ///   <item>Otherwise → <see cref="StepStatus.Succeeded"/>.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public static string ComputeTerminalStatus(
        IFlowDefinition flow,
        IReadOnlyDictionary<string, StepStatus> statuses)
    {
        var anySucceeded = false;
        var anyFailed = false;
        var anySkipped = false;
        foreach (var s in statuses.Values)
        {
            switch (s)
            {
                case StepStatus.Succeeded: anySucceeded = true; break;
                case StepStatus.Failed:    anyFailed    = true; break;
                case StepStatus.Skipped:   anySkipped   = true; break;
            }
        }

        if (!anySucceeded)
        {
            return anyFailed
                ? StepStatus.Failed.ToString()
                : anySkipped
                    ? StepStatus.Skipped.ToString()
                    : StepStatus.Failed.ToString();
        }

        if (anyFailed && AnyUnhandledFailure(flow, statuses))
        {
            return StepStatus.Failed.ToString();
        }

        // Determine "all leaves skipped". A leaf is a step that nothing else in the run
        // depends on — neither a downstream RunAfter edge nor a structural child (loop
        // iteration). We want: at least one leaf && every leaf is Skipped.
        var nonLeaves = CollectNonLeafKeys(flow, statuses);
        var anyLeaf = false;
        foreach (var (key, status) in statuses)
        {
            if (nonLeaves.Contains(StepKeySlice.Of(key)))
            {
                continue;
            }

            if (status != StepStatus.Skipped)
            {
                return StepStatus.Succeeded.ToString();
            }

            anyLeaf = true;
        }

        return anyLeaf ? StepStatus.Skipped.ToString() : StepStatus.Succeeded.ToString();
    }

    /// <summary>
    /// Returns <see langword="true"/> when some failed step has no downstream step that ran and
    /// <see cref="StepStatus.Succeeded"/> — i.e. no explicit recovery handler.
    /// </summary>
    /// <remarks>
    /// One pass collects every key that a succeeded step depends on, resolved into runtime space
    /// (a sibling dependency declared as the bare template key <c>child</c> on step
    /// <c>loop.0.next</c> resolves to <c>loop.0.child</c>); a failed step is handled exactly when its
    /// key is in that set. O(n · d).
    /// </remarks>
    private static bool AnyUnhandledFailure(IFlowDefinition flow, IReadOnlyDictionary<string, StepStatus> statuses)
    {
        var handled = new HashSet<StepKeySlice>(StepKeySliceComparer.Instance);
        foreach (var (key, status) in statuses)
        {
            if (status == StepStatus.Succeeded)
            {
                AddResolvedDependencies(flow, key, handled, excludeSelf: false);
            }
        }

        foreach (var (key, status) in statuses)
        {
            if (status == StepStatus.Failed && !handled.Contains(StepKeySlice.Of(key)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the set of keys that something else in the run depends on: every step's
    /// <c>RunAfter</c> dependencies resolved into runtime space, plus every dotted ancestor of every
    /// runtime key (a loop iteration <c>loop.0.child</c> makes <c>loop</c> and <c>loop.0</c>
    /// non-leaves even though no <c>RunAfter</c> edge connects them).
    /// </summary>
    /// <remarks>
    /// A step depending on itself does not make itself a non-leaf — the previous per-pair scan
    /// skipped the key under test as a candidate, and this preserves that.
    /// </remarks>
    private static HashSet<StepKeySlice> CollectNonLeafKeys(IFlowDefinition flow, IReadOnlyDictionary<string, StepStatus> statuses)
    {
        var nonLeaves = new HashSet<StepKeySlice>(statuses.Count, StepKeySliceComparer.Instance);
        foreach (var key in statuses.Keys)
        {
            // Structural ancestors: every prefix that ends just before a '.'. The prefix ending at
            // index 0 is the empty key, which the per-pair test also treated as an owner of any key
            // starting with '.'.
            for (var dot = key.IndexOf('.'); dot >= 0; dot = key.IndexOf('.', dot + 1))
            {
                nonLeaves.Add(new StepKeySlice(key, dot));
            }

            AddResolvedDependencies(flow, key, nonLeaves, excludeSelf: true);
        }

        return nonLeaves;
    }

    /// <summary>
    /// Adds the runtime-space key of every <c>RunAfter</c> dependency declared by the step at
    /// <paramref name="dependentKey"/> to <paramref name="target"/>.
    /// </summary>
    /// <param name="flow">The flow whose manifest declares the dependencies.</param>
    /// <param name="dependentKey">Runtime key of the dependent step.</param>
    /// <param name="target">Set receiving the resolved dependency keys.</param>
    /// <param name="excludeSelf">When set, a dependency resolving to <paramref name="dependentKey"/> itself is not added.</param>
    private static void AddResolvedDependencies(
        IFlowDefinition flow,
        string dependentKey,
        HashSet<StepKeySlice> target,
        bool excludeSelf)
    {
        var metadata = flow.Manifest.Steps.FindStep(dependentKey);
        if (metadata?.RunAfter is null || metadata.RunAfter.Count == 0)
        {
            return;
        }

        foreach (var dependency in metadata.RunAfter.Keys)
        {
            if (string.IsNullOrEmpty(dependency))
            {
                continue;
            }

            var resolved = ResolveRuntimeDependencyKey(dependentKey, dependency);
            if (excludeSelf && StepKeySliceComparer.Instance.Equals(resolved, StepKeySlice.Of(dependentKey)))
            {
                continue;
            }

            target.Add(resolved);
        }
    }

    /// <summary>
    /// Resolves a (possibly template) <c>RunAfter</c> dependency key to its runtime equivalent
    /// by prepending the dependent step's loop-scope prefix when the dependency is a sibling
    /// (no dot, same scope level). Mirrors <c>FlowGraphPlanner.ResolveRuntimeDependencyKey</c>.
    /// </summary>
    /// <remarks>
    /// Returns a <see cref="StepKeySlice"/> over the two existing strings rather than the
    /// interpolated <c>$"{parent}.{dependency}"</c> the per-pair scan built on every comparison.
    /// </remarks>
    private static StepKeySlice ResolveRuntimeDependencyKey(string runtimeStepKey, string dependencyKey)
    {
        if (dependencyKey.Contains('.', StringComparison.Ordinal))
        {
            return StepKeySlice.Of(dependencyKey);
        }

        var lastDot = runtimeStepKey.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return StepKeySlice.Of(dependencyKey);
        }

        return new StepKeySlice(runtimeStepKey, lastDot, dependencyKey);
    }
}
