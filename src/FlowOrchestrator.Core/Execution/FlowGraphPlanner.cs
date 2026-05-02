using System.Runtime.CompilerServices;
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Default <see cref="IFlowGraphPlanner"/> that evaluates the flow's step DAG
/// to determine execution order, supporting nested scoped steps and runtime loop keys.
/// </summary>
public sealed class FlowGraphPlanner : IFlowGraphPlanner
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

    /// <inheritdoc/>
    public FlowGraphValidationResult Validate(IFlowDefinition flow)
    {
        var errors = new List<string>();
        var flat = FlattenTemplateSteps(flow.Manifest.Steps);

        if (flat.Count == 0)
        {
            errors.Add("Flow has no steps.");
            return new FlowGraphValidationResult { Errors = errors };
        }

        if (!flat.Any(kvp => kvp.Value.RunAfter.Count == 0))
        {
            errors.Add("Flow has no entry step (step with empty runAfter).");
        }

        foreach (var (stepKey, metadata) in flat)
        {
            if (string.IsNullOrWhiteSpace(metadata.Type))
            {
                errors.Add($"Step '{stepKey}' has empty type.");
            }

            foreach (var dependency in metadata.RunAfter.Keys)
            {
                var templateDependency = ResolveTemplateDependencyKey(stepKey, dependency);
                if (!flat.ContainsKey(templateDependency))
                {
                    errors.Add($"Step '{stepKey}' depends on missing step '{dependency}'.");
                }
            }
        }

        var hasCycle = HasCycle(flat);
        if (hasCycle)
        {
            errors.Add("Flow has a dependency cycle.");
        }

        return new FlowGraphValidationResult { Errors = errors };
    }

    /// <summary>
    /// Depth-first cycle detection using a "visiting" set.
    /// A cycle is detected when a node is encountered that is already in the current DFS path.
    /// </summary>
    private static bool HasCycle(IReadOnlyDictionary<string, StepMetadata> flat)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        bool Visit(string key)
        {
            if (visited.Contains(key))
            {
                return false;
            }

            if (!visiting.Add(key))
            {
                return true;
            }

            var metadata = flat[key];
            foreach (var dep in metadata.RunAfter.Keys.Select(d => ResolveTemplateDependencyKey(key, d)))
            {
                if (flat.ContainsKey(dep) && Visit(dep))
                {
                    return true;
                }
            }

            visiting.Remove(key);
            visited.Add(key);
            return false;
        }

        foreach (var key in flat.Keys)
        {
            if (Visit(key))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, StepMetadata> FlattenTemplateSteps(StepCollection collection)
    {
        var result = new Dictionary<string, StepMetadata>(StringComparer.Ordinal);
        Flatten(collection, null, result);
        return result;
    }

    private static void Flatten(
        IReadOnlyDictionary<string, StepMetadata> current,
        string? prefix,
        IDictionary<string, StepMetadata> target)
    {
        foreach (var (key, step) in current)
        {
            var fullKey = string.IsNullOrEmpty(prefix) ? key : $"{prefix}.{key}";
            target[fullKey] = step;

            if (step is IScopedStep scoped && scoped.Steps.Count > 0)
            {
                Flatten(scoped.Steps, fullKey, target);
            }
        }
    }

    private static IReadOnlyList<string> BuildKnownStepKeys(IFlowDefinition flow, IEnumerable<string> runtimeStepKeys)
    {
        var cache = GetManifestCache(flow);

        // Fast path: a flow without loops/foreach has runtime keys that are
        // all already in the manifest. In that case the known-key set is
        // exactly the manifest's sorted keys — return the cached array
        // directly. Avoids the SortedSet allocation, the sort, and the
        // ToArray copy on every call.
        var needsExpansion = false;
        foreach (var runtimeKey in runtimeStepKeys)
        {
            if (!cache.KeySet.Contains(runtimeKey))
            {
                needsExpansion = true;
                break;
            }
        }

        if (!needsExpansion)
        {
            return cache.SortedKeys;
        }

        // Slow path: at least one runtime key is from a scope expansion
        // (e.g. "loop.0.child"). Build the full sorted set as before.
        var known = new SortedSet<string>(cache.KeySet, StringComparer.Ordinal);
        foreach (var runtimeKey in runtimeStepKeys)
        {
            known.Add(runtimeKey);

            foreach (var (scopePrefix, scopedMetadata) in ExtractRuntimeScopePrefixes(flow, runtimeKey))
            {
                foreach (var child in scopedMetadata.Steps.Keys)
                {
                    known.Add($"{scopePrefix}.{child}");
                }
            }
        }

        return known.ToArray();
    }

    private static IEnumerable<(string RuntimeScopePrefix, IScopedStep ScopedMetadata)> ExtractRuntimeScopePrefixes(
        IFlowDefinition flow,
        string runtimeStepKey)
    {
        var segments = runtimeStepKey
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length < 2)
        {
            yield break;
        }

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!int.TryParse(segments[i + 1], out _))
            {
                continue;
            }

            var runtimeTemplatePath = string.Join('.', segments.Take(i + 1));
            var templatePath = RemoveNumericSegments(runtimeTemplatePath);
            var metadata = flow.Manifest.Steps.FindStep(templatePath);
            if (metadata is not IScopedStep scoped)
            {
                continue;
            }

            var runtimeScopePrefix = string.Join('.', segments.Take(i + 2));
            yield return (runtimeScopePrefix, scoped);
        }
    }

    private static StepMetadata? ResolveMetadata(IFlowDefinition flow, string runtimeStepKey)
    {
        var metadata = flow.Manifest.Steps.FindStep(runtimeStepKey);
        if (metadata is not null)
        {
            return metadata;
        }

        var templateKey = RemoveNumericSegments(runtimeStepKey);
        return flow.Manifest.Steps.FindStep(templateKey);
    }

    /// <summary>
    /// Resolves a dependency key to its runtime equivalent by prepending the current step's
    /// loop-scope prefix when the dependency is a sibling step (no dot, same scope level).
    /// </summary>
    private static string ResolveRuntimeDependencyKey(string runtimeStepKey, string dependencyKey)
    {
        if (string.IsNullOrWhiteSpace(dependencyKey))
        {
            return dependencyKey;
        }

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

    /// <summary>
    /// Resolves a dependency key to its template (non-runtime) equivalent, removing
    /// numeric iteration segments so it can be matched against the manifest definition.
    /// </summary>
    private static string ResolveTemplateDependencyKey(string stepKey, string dependencyKey)
    {
        if (string.IsNullOrWhiteSpace(dependencyKey))
        {
            return dependencyKey;
        }

        if (dependencyKey.Contains('.', StringComparison.Ordinal))
        {
            return RemoveNumericSegments(dependencyKey);
        }

        var lastDot = stepKey.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return dependencyKey;
        }

        var parentPath = stepKey[..lastDot];
        return $"{parentPath}.{dependencyKey}";
    }

    /// <summary>Removes numeric loop-iteration segments from a dot-separated step key.</summary>
    private static string RemoveNumericSegments(string key)
    {
        var segments = key
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => !int.TryParse(segment, out _));

        return string.Join('.', segments);
    }

    private static bool IsFinal(StepStatus status) =>
        status is StepStatus.Succeeded or StepStatus.Failed or StepStatus.Skipped;

    private static StepInstance CreateStepInstance(
        IExecutionContext context,
        string key,
        string type,
        IDictionary<string, object?> inputs)
    {
        return new StepInstance(key, type)
        {
            RunId = context.RunId,
            PrincipalId = context.PrincipalId,
            TriggerData = context.TriggerData,
            TriggerHeaders = context.TriggerHeaders,
            ScheduledTime = DateTimeOffset.UtcNow,
            Inputs = new Dictionary<string, object?>(inputs)
        };
    }
}
