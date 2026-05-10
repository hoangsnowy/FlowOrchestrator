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
    /// <see cref="BuildKnownStepKeys"/> is called on every step completion via
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
        var known = BuildKnownStepKeys(flow, statuses.Keys);
        var ready = new List<string>();
        var blocked = new List<string>();
        var waiting = new List<string>();

        foreach (var stepKey in known)
        {
            if (statuses.ContainsKey(stepKey))
            {
                continue;
            }

            var metadata = ResolveMetadata(flow, stepKey);
            if (metadata is null)
            {
                continue;
            }

            if (metadata.RunAfter.Count == 0)
            {
                ready.Add(stepKey);
                continue;
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

        return new FlowGraphEvaluation
        {
            ReadyStepKeys = ready,
            BlockedStepKeys = blocked,
            WaitingStepKeys = waiting,
            AllKnownStepKeys = known
        };
    }
}
