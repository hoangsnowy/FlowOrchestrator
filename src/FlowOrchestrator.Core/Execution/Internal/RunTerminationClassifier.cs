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
    /// A single foreach over <paramref name="statuses"/> populates three boolean tally
    /// flags up-front so the subsequent rule cascade does not re-enumerate the dictionary
    /// for trivial existence checks. The downstream leaf-collection pass materialises a
    /// small intermediate list; the trade-off was made to keep CodeQL's
    /// <c>cs/linq/missed-where</c> analysis quiet. Leaf and failure-handling detection
    /// operate on runtime step keys (e.g. <c>loop.0.child</c>): a dependent's template
    /// <c>RunAfter</c> keys are resolved into runtime space and a scoped step is treated as
    /// a non-leaf whenever its iteration children are present, so loop bodies classify from
    /// their children rather than from the loop step's own status.
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

        if (anyFailed && statuses.Any(kvp =>
                kvp.Value == StepStatus.Failed
                && !IsFailureHandled(kvp.Key, flow, statuses)))
        {
            return StepStatus.Failed.ToString();
        }

        // Determine "all leaves skipped". A leaf is a step that nothing else in the run
        // depends on — neither a downstream RunAfter edge nor a structural child (loop
        // iteration). We want: at least one leaf && every leaf is Skipped.
        var leafEntries = statuses
            .Where(kvp => IsLeaf(kvp.Key, flow, statuses))
            .ToList();

        return (leafEntries.Count > 0 && leafEntries.TrueForAll(kvp => kvp.Value == StepStatus.Skipped))
            ? StepStatus.Skipped.ToString()
            : StepStatus.Succeeded.ToString();
    }

    /// <summary>
    /// Returns <see langword="true"/> when a failed step has at least one downstream step
    /// that ran and <see cref="StepStatus.Succeeded"/>, indicating an explicit recovery handler.
    /// </summary>
    /// <remarks>
    /// Dependents are searched over the run's actual step keys (which may be runtime loop
    /// keys such as <c>loop.0.child</c>), and each dependent's template <c>RunAfter</c> keys
    /// are resolved into runtime space before comparison — a sibling dependency declared as
    /// the bare template key <c>child</c> on step <c>loop.0.next</c> resolves to the runtime
    /// key <c>loop.0.child</c>. Without this resolution a recovery handler inside a loop body
    /// would never match its failed predecessor.
    /// </remarks>
    private static bool IsFailureHandled(
        string failedStepKey,
        IFlowDefinition flow,
        IReadOnlyDictionary<string, StepStatus> statuses)
    {
        foreach (var kvp in statuses)
        {
            if (kvp.Value != StepStatus.Succeeded)
            {
                continue;
            }

            if (DependsOn(kvp.Key, failedStepKey, flow))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when nothing else in the run depends on
    /// <paramref name="key"/> — neither a downstream <c>RunAfter</c> edge nor a structural
    /// child (a loop iteration whose runtime key is nested under <paramref name="key"/>).
    /// Such a step is a leaf of the executed graph.
    /// </summary>
    /// <remarks>
    /// Operates on runtime step keys. A loop step (e.g. <c>loop</c>) is therefore correctly
    /// excluded from the leaf set whenever its iteration children (<c>loop.0.child</c>) are
    /// present, so an all-skipped loop body classifies as <see cref="StepStatus.Skipped"/>
    /// rather than borrowing the loop step's own <see cref="StepStatus.Succeeded"/> status.
    /// </remarks>
    private static bool IsLeaf(string key, IFlowDefinition flow, IReadOnlyDictionary<string, StepStatus> statuses)
    {
        foreach (var candidateKey in statuses.Keys)
        {
            if (string.Equals(candidateKey, key, StringComparison.Ordinal))
            {
                continue;
            }

            // Structural child: a loop iteration step (e.g. "loop.0.child") makes its
            // owning scoped step ("loop") a non-leaf even though no RunAfter edge connects them.
            if (IsStructuralDescendant(candidateKey, key))
            {
                return false;
            }

            if (DependsOn(candidateKey, key, flow))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="dependentKey"/> declares
    /// <paramref name="predecessorKey"/> as a <c>RunAfter</c> dependency, accounting for the
    /// loop-scope prefix so a sibling dependency expressed as a bare template key matches the
    /// runtime predecessor in the same iteration scope.
    /// </summary>
    private static bool DependsOn(string dependentKey, string predecessorKey, IFlowDefinition flow)
    {
        var metadata = flow.Manifest.Steps.FindStep(dependentKey);
        if (metadata?.RunAfter is null || metadata.RunAfter.Count == 0)
        {
            return false;
        }

        foreach (var dependency in metadata.RunAfter.Keys)
        {
            if (string.IsNullOrEmpty(dependency))
            {
                continue;
            }

            if (string.Equals(ResolveRuntimeDependencyKey(dependentKey, dependency), predecessorKey, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="candidateKey"/> is nested strictly
    /// beneath <paramref name="ownerKey"/> in the dot-separated runtime key hierarchy
    /// (e.g. <c>loop.0.child</c> is a descendant of <c>loop</c>).
    /// </summary>
    private static bool IsStructuralDescendant(string candidateKey, string ownerKey)
    {
        return candidateKey.Length > ownerKey.Length
            && candidateKey[ownerKey.Length] == '.'
            && candidateKey.StartsWith(ownerKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves a (possibly template) <c>RunAfter</c> dependency key to its runtime equivalent
    /// by prepending the dependent step's loop-scope prefix when the dependency is a sibling
    /// (no dot, same scope level). Mirrors <c>FlowGraphPlanner.ResolveRuntimeDependencyKey</c>.
    /// </summary>
    private static string ResolveRuntimeDependencyKey(string runtimeStepKey, string dependencyKey)
    {
        if (dependencyKey.Contains('.', StringComparison.Ordinal))
        {
            return dependencyKey;
        }

        var lastDot = runtimeStepKey.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return dependencyKey;
        }

        var runtimeParentPath = runtimeStepKey[..lastDot];
        return $"{runtimeParentPath}.{dependencyKey}";
    }
}
