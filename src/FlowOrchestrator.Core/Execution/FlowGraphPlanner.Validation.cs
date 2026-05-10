using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Validation surface of <see cref="FlowGraphPlanner"/> — manifest sanity checks,
/// missing-dependency detection, and DFS cycle detection.
/// </summary>
public sealed partial class FlowGraphPlanner
{
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
}
