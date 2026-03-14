namespace FlowOrchestrator.Core.Abstractions;

public sealed class StepCollection : Dictionary<string, StepMetadata>
{
    public StepMetadata? FindStep(string key)
    {
        if (TryGetValue(key, out var step))
        {
            return step;
        }

        // Support nested keys: "parent.child"
        var segments = key.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length <= 1)
        {
            return null;
        }

        return FindStepRecursive(this, segments, 0);
    }

    private static StepMetadata? FindStepRecursive(IDictionary<string, StepMetadata> current, IReadOnlyList<string> segments, int index)
    {
        if (index >= segments.Count)
        {
            return null;
        }

        if (!current.TryGetValue(segments[index], out var step) || step is null)
        {
            return null;
        }

        if (index == segments.Count - 1)
        {
            return step;
        }

        if (step is IScopedStep scoped && scoped.Steps is { Count: > 0 })
        {
            return FindStepRecursive(scoped.Steps, segments, index + 1);
        }

        return null;
    }

    public StepMetadata? FindNextStep(string currentKey)
    {
        foreach (var kvp in this)
        {
            var metadata = kvp.Value;
            if (ReferenceEquals(metadata, null))
            {
                continue;
            }

            if (metadata.RunAfter.TryGetValue(currentKey, out var statuses) && statuses is { Length: > 0 })
            {
                return metadata;
            }
        }

        return null;
    }

    public StepMetadata? FindParentStep(string key)
    {
        var segments = key.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length <= 1)
        {
            return null;
        }

        var parentSegments = segments.Take(segments.Length - 1).ToArray();
        return FindStep(string.Join('.', parentSegments));
    }
}
