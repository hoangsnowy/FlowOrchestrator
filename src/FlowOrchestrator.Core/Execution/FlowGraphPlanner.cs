using System.Runtime.CompilerServices;
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Default <see cref="IFlowGraphPlanner"/> that evaluates the flow's step DAG
/// to determine execution order, supporting nested scoped steps and runtime loop keys.
/// </summary>
/// <remarks>
/// The implementation is split across three partial files for readability —
/// the public surface and DAG evaluation live here, validation lives in
/// <c>FlowGraphPlanner.Validation.cs</c>, and key/scope resolution helpers
/// live in <c>FlowGraphPlanner.KeyResolution.cs</c>.
/// </remarks>
public sealed partial class FlowGraphPlanner : IFlowGraphPlanner
{
    /// <summary>
    /// Cached pre-sorted manifest step keys per flow definition.
    /// </summary>
    /// <remarks>
    /// <see cref="PlanCandidateStepKeys"/> is called on every step completion via
    /// <see cref="Evaluate"/>. The flow manifest is immutable post-startup, so
    /// the sorted set of its step keys can be computed once per flow and reused.
    /// For linear flows without loops or foreach (the common case),
    /// <c>statuses.Keys</c> is a subset of the manifest keys and the cached
    /// array can be returned directly — no SortedSet allocation, no
    /// per-call sort.
    /// <para>
    /// <see cref="ConditionalWeakTable{TKey,TValue}"/> keys on the
    /// <see cref="IFlowDefinition"/> reference and lets the GC reclaim the
    /// cache entry when the flow is unregistered.
    /// </para>
    /// </remarks>
    private sealed class ManifestKeyCache
    {
        public required string[] SortedKeys { get; init; }
        public required HashSet<string> KeySet { get; init; }
    }

    private static readonly ConditionalWeakTable<IFlowDefinition, ManifestKeyCache> _manifestCache = new();

    private static ManifestKeyCache GetManifestCache(IFlowDefinition flow)
    {
        return _manifestCache.GetValue(flow, static f =>
        {
            var sortedKeys = f.Manifest.Steps.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            var keySet = new HashSet<string>(sortedKeys, StringComparer.Ordinal);
            return new ManifestKeyCache { SortedKeys = sortedKeys, KeySet = keySet };
        });
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A step is an entry step when its <c>RunAfter</c> is empty OR contains only the
    /// synthetic empty-string key (<c>RunAfter[""]</c>) used to attach a <c>When</c>
    /// clause directly to the trigger payload.
    /// </remarks>
    public IReadOnlyList<IStepInstance> CreateEntrySteps(ITriggerContext context)
    {
        var entries = context.Flow.Manifest.Steps
            .Where(kvp => kvp.Value is { } meta && IsEntryStep(meta))
            .Select(kvp => CreateStepInstance(context, kvp.Key, kvp.Value.Type, kvp.Value.Inputs))
            .Cast<IStepInstance>()
            .ToList();

        return entries;
    }

    private static bool IsEntryStep(StepMetadata meta)
    {
        if (meta.RunAfter is null || meta.RunAfter.Count == 0)
        {
            return true;
        }
        // Entry-with-condition: RunAfter contains only the synthetic "" key.
        return meta.RunAfter.Count == 1 && meta.RunAfter.ContainsKey(string.Empty);
    }

    /// <inheritdoc/>
    public FlowGraphEvaluation Evaluate(IFlowDefinition flow, IReadOnlyDictionary<string, StepStatus> statuses)
    {
        // O(n) in the status-map size, no sort over n — see PlanCandidateStepKeys. The loop below is
        // over the candidates only: known keys that have no status row yet.
        var candidates = PlanCandidateStepKeys(flow, statuses);
        var ready = new List<string>();
        var blocked = new List<string>();
        var waiting = new List<string>();

        foreach (var stepKey in candidates)
        {
            Classify(flow, stepKey, statuses, ready, blocked, waiting);
        }

        return new FlowGraphEvaluation
        {
            ReadyStepKeys = ready,
            BlockedStepKeys = blocked,
            WaitingStepKeys = waiting
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Classifies exactly the candidates, with the same rule as <see cref="Evaluate"/>: a candidate's verdict
    /// depends only on its own row and its dependencies' rows, all of which the caller read.
    /// </para>
    /// <para>
    /// <b>Complexity per step completion:</b> O(c log c + Σ fan-in) for c candidates — the dependents of the
    /// step that changed — and no term in the number of status rows in the run. The sort keeps the dispatch
    /// order identical to <see cref="Evaluate"/>'s ordinal order.
    /// </para>
    /// </remarks>
    public FlowGraphEvaluation EvaluateFrontier(
        IFlowDefinition flow,
        IReadOnlyCollection<string> candidateStepKeys,
        IReadOnlyDictionary<string, StepStatus> statuses)
    {
        var ready = new List<string>();
        var blocked = new List<string>();
        var waiting = new List<string>();

        if (candidateStepKeys.Count > 0)
        {
            var ordered = new string[candidateStepKeys.Count];
            var count = 0;
            foreach (var key in candidateStepKeys)
            {
                ordered[count++] = key;
            }

            Array.Sort(ordered, StringComparer.Ordinal);
            string? previous = null;
            foreach (var stepKey in ordered)
            {
                if (string.Equals(stepKey, previous, StringComparison.Ordinal))
                {
                    continue;
                }

                previous = stepKey;
                Classify(flow, stepKey, statuses, ready, blocked, waiting);
            }
        }

        return new FlowGraphEvaluation
        {
            ReadyStepKeys = ready,
            BlockedStepKeys = blocked,
            WaitingStepKeys = waiting
        };
    }

    /// <summary>
    /// Appends the runtime keys of <paramref name="runtimeStepKey"/>'s dependencies — resolved exactly as
    /// <see cref="Evaluate"/> resolves them — to <paramref name="dependencyKeys"/>.
    /// </summary>
    /// <param name="flow">The flow whose manifest declares the step.</param>
    /// <param name="runtimeStepKey">A runtime step key, e.g. <c>"loop.3.b"</c>.</param>
    /// <param name="dependencyKeys">Receives the dependencies' runtime keys; the synthetic entry key is skipped.</param>
    /// <remarks>O(fan-in of the step) — one key resolution per declared dependency.</remarks>
    internal static void AddRuntimeDependencyKeys(IFlowDefinition flow, string runtimeStepKey, ICollection<string> dependencyKeys)
    {
        var metadata = ResolveMetadata(flow, runtimeStepKey);
        if (metadata is null)
        {
            return;
        }

        // foreach on purpose (CLAUDE.md hot-path rule): per step completion, no closure; CodeQL cs/linq/missed-where dismissed.
        foreach (var dependency in metadata.RunAfter.Keys)
        {
            if (!string.IsNullOrEmpty(dependency))
            {
                dependencyKeys.Add(ResolveRuntimeDependencyKey(runtimeStepKey, dependency));
            }
        }
    }

    /// <summary>
    /// Puts <paramref name="stepKey"/> in <paramref name="ready"/>, <paramref name="blocked"/> or
    /// <paramref name="waiting"/>, or nowhere when it already has a status row or does not resolve to a step.
    /// </summary>
    private static void Classify(
        IFlowDefinition flow,
        string stepKey,
        IReadOnlyDictionary<string, StepStatus> statuses,
        List<string> ready,
        List<string> blocked,
        List<string> waiting)
    {
        if (statuses.ContainsKey(stepKey))
        {
            return;
        }

        var metadata = ResolveMetadata(flow, stepKey);
        if (metadata is null)
        {
            return;
        }

        if (metadata.RunAfter.Count == 0)
        {
            ready.Add(stepKey);
            return;
        }

        var allSatisfied = true;
        var hasFinalMismatch = false;
        foreach (var dependency in metadata.RunAfter)
        {
            // Synthetic entry-trigger key — no real predecessor, status gate is vacuously satisfied.
            if (string.IsNullOrEmpty(dependency.Key))
            {
                continue;
            }

            var runtimeDependencyKey = ResolveRuntimeDependencyKey(stepKey, dependency.Key);
            if (!statuses.TryGetValue(runtimeDependencyKey, out var dependencyStatus))
            {
                allSatisfied = false;
                continue;
            }

            if (dependency.Value.AcceptsStatus(dependencyStatus))
            {
                continue;
            }

            allSatisfied = false;
            if (IsFinal(dependencyStatus))
            {
                hasFinalMismatch = true;
            }
        }

        if (allSatisfied)
        {
            ready.Add(stepKey);
        }
        else if (hasFinalMismatch)
        {
            blocked.Add(stepKey);
        }
        else
        {
            waiting.Add(stepKey);
        }
    }
}
