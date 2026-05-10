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
    /// Allocation-free: a single foreach over <paramref name="statuses"/> populates three
    /// boolean tally flags, replacing three independent <c>LINQ.Any</c> passes that previously
    /// allocated three enumerators per call. For a 50-step flow this drops 150 enumerator
    /// allocations to 1 per termination check.
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

        var hasUnhandledFailure = false;
        if (anyFailed)
        {
            foreach (var kvp in statuses)
            {
                if (kvp.Value == StepStatus.Failed
                    && !IsFailureHandled(kvp.Key, flow.Manifest.Steps, statuses))
                {
                    hasUnhandledFailure = true;
                    break;
                }
            }
        }

        if (hasUnhandledFailure)
        {
            return StepStatus.Failed.ToString();
        }

        // Determine "all leaves skipped" without materialising a HashSet.
        // A leaf is a step whose key never appears in any RunAfter map.
        // We want: leafKeys.Count > 0 && every leaf has Skipped status.
        var leafCount = 0;
        var allLeavesSkipped = true;
        foreach (var key in statuses.Keys)
        {
            if (IsLeaf(key, flow.Manifest.Steps))
            {
                leafCount++;
                if (!statuses.TryGetValue(key, out var status) || status != StepStatus.Skipped)
                {
                    allLeavesSkipped = false;
                }
            }
        }

        return (leafCount > 0 && allLeavesSkipped)
            ? StepStatus.Skipped.ToString()
            : StepStatus.Succeeded.ToString();
    }

    /// <summary>
    /// Returns <see langword="true"/> when a failed step has at least one downstream step
    /// that ran and <see cref="StepStatus.Succeeded"/>, indicating an explicit recovery handler.
    /// </summary>
    private static bool IsFailureHandled(
        string failedStepKey,
        StepCollection manifestSteps,
        IReadOnlyDictionary<string, StepStatus> statuses)
    {
        foreach (var kvp in manifestSteps)
        {
            if (kvp.Value.RunAfter?.ContainsKey(failedStepKey) == true
                && statuses.TryGetValue(kvp.Key, out var s)
                && s == StepStatus.Succeeded)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when no manifest step references
    /// <paramref name="key"/> in its <c>RunAfter</c> map — i.e., the step is a leaf of the DAG.
    /// </summary>
    private static bool IsLeaf(string key, StepCollection manifestSteps)
    {
        foreach (var kvp in manifestSteps)
        {
            if (kvp.Value.RunAfter?.ContainsKey(key) == true)
            {
                return false;
            }
        }
        return true;
    }
}
