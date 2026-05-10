using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Step-key and scope-prefix resolution helpers for <see cref="FlowGraphPlanner"/>.
/// Bridges between the manifest's template keys and the runtime keys produced by
/// loop / foreach iteration (e.g. <c>"loop.0.child"</c> vs <c>"loop.child"</c>).
/// </summary>
public sealed partial class FlowGraphPlanner
{
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
