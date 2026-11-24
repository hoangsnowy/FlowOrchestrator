using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Validation surface of <see cref="FlowGraphPlanner"/> — manifest sanity checks,
/// step-key shape checks, missing-dependency detection, and DFS cycle detection.
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

        // Runtime step keys are '.'-joined paths ("loop.3.child"), so a top-level key that is empty
        // or itself contains '.' is ambiguous with a scoped path and is not modelled by Evaluate.
        foreach (var stepKey in flow.Manifest.Steps.Keys)
        {
            if (stepKey.Length == 0)
            {
                errors.Add("Flow has a step with an empty key.");
            }
            else if (stepKey.Contains('.', StringComparison.Ordinal))
            {
                errors.Add($"Step key '{stepKey}' contains '.', which is reserved as the runtime scope separator.");
            }
        }

        ValidateStorageKeyShape(flow.Manifest.Steps, null, errors);

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
    /// Maximum length, in characters, of a step key's flattened template path (<c>"loop.child"</c>).
    /// </summary>
    /// <remarks>
    /// Every store persists a step's resolved inputs under <c>"{stepKey}:input"</c> in a
    /// 256-character key column (<c>FlowOutputs.[Key]</c> on SQL Server, <c>flow_outputs.key</c> on
    /// PostgreSQL), and the step-state tables key on a 256-character <c>StepKey</c>. 250 leaves room for
    /// the <c>":input"</c> suffix. <c>ForEach</c> iteration indices (<c>"loop.12.child"</c>) are added
    /// at runtime on top of this limit, so keys inside large loops should stay well below it.
    /// </remarks>
    public const int MaxStepKeyLength = 250;

    /// <summary>
    /// Checks the step-key rules that come from how the stores persist keys, at every nesting level:
    /// no <c>':'</c>, no keys that differ only by case within one scope, and the length cap.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>':'</c> is the separator of the outputs-key namespace (<c>"{key}:input"</c>,
    /// <c>"__trigger:data"</c>), so a step named <c>"x:input"</c> would overwrite step <c>x</c>'s inputs.</item>
    /// <item><see cref="StepCollection"/> is ordinal, but SQL Server's default collation is
    /// case-insensitive on every <c>StepKey</c> primary key, so <c>"Notify"</c> and <c>"notify"</c> would
    /// share one claim, one dispatch row and one status row there and the run would never finish.
    /// Rejecting the pair here gives every provider the same contract.</item>
    /// </list>
    /// Startup-only: runs once per flow from <c>FlowSyncHostedService</c>, never on the step path.
    /// </remarks>
    private static void ValidateStorageKeyShape(StepCollection scope, string? prefix, List<string> errors)
    {
        var seen = new Dictionary<string, string>(scope.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, step) in scope)
        {
            var fullKey = string.IsNullOrEmpty(prefix) ? key : $"{prefix}.{key}";

            if (key.Contains(':', StringComparison.Ordinal))
            {
                errors.Add($"Step key '{fullKey}' contains ':', which is reserved as the outputs-key separator (\"{{stepKey}}:input\").");
            }

            if (fullKey.Length > MaxStepKeyLength)
            {
                errors.Add($"Step key '{fullKey}' is {fullKey.Length} characters long; the maximum is {MaxStepKeyLength} (the stores persist \"{{stepKey}}:input\" in a 256-character key column).");
            }

            if (!seen.TryAdd(key, key))
            {
                var other = string.IsNullOrEmpty(prefix) ? seen[key] : $"{prefix}.{seen[key]}";
                errors.Add($"Step keys '{other}' and '{fullKey}' differ only by case; step keys must be unique ignoring case within a scope, because SQL Server compares them case-insensitively.");
            }

            if (step is IScopedStep scoped && scoped.Steps.Count > 0)
            {
                ValidateStorageKeyShape(scoped.Steps, fullKey, errors);
            }
        }
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
            var resolvedDeps = metadata.RunAfter.Keys
                .Select(d => ResolveTemplateDependencyKey(key, d))
                .Where(dep => flat.ContainsKey(dep));
            if (resolvedDeps.Any(Visit))
            {
                return true;
            }

            visiting.Remove(key);
            visited.Add(key);
            return false;
        }

        return flat.Keys.Any(Visit);
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
