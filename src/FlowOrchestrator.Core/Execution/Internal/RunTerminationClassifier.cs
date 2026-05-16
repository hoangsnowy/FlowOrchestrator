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
    /// for trivial existence checks. The downstream leaf-collection and failure-handling
    /// passes use LINQ expressions that materialise small intermediate collections; the
    /// trade-off was made to keep CodeQL's <c>cs/linq/missed-where</c> analysis quiet.
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
                && !IsFailureHandled(kvp.Key, flow.Manifest.Steps, statuses)))
        {
            return StepStatus.Failed.ToString();
        }

        // Determine "all leaves skipped". A leaf is a step whose key never appears
        // in any RunAfter map. We want: at least one leaf && every leaf is Skipped.
        var leafEntries = statuses
            .Where(kvp => IsLeaf(kvp.Key, flow.Manifest.Steps))
            .ToList();

        return (leafEntries.Count > 0 && leafEntries.TrueForAll(kvp => kvp.Value == StepStatus.Skipped))
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
        return manifestSteps.Any(kvp =>
            kvp.Value.RunAfter?.ContainsKey(failedStepKey) == true
            && statuses.TryGetValue(kvp.Key, out var s)
            && s == StepStatus.Succeeded);
    }

    /// <summary>
    /// Returns <see langword="true"/> when no manifest step references
    /// <paramref name="key"/> in its <c>RunAfter</c> map — i.e., the step is a leaf of the DAG.
    /// </summary>
    private static bool IsLeaf(string key, StepCollection manifestSteps)
    {
        return !manifestSteps.Any(kvp => kvp.Value.RunAfter?.ContainsKey(key) == true);
    }
}
