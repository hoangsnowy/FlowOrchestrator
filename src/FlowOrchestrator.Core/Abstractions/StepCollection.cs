namespace FlowOrchestrator.Core.Abstractions;

/// <summary>
/// Dictionary of <see cref="StepMetadata"/> keyed by step name, with helpers for
/// navigating nested step hierarchies using dot-notation paths.
/// </summary>
public sealed class StepCollection : Dictionary<string, StepMetadata>
{
    /// <summary>
    /// Finds a step by its key, supporting nested and runtime-indexed paths.
    /// </summary>
    /// <param name="key">
    /// A dot-separated path such as <c>"processItems"</c>, <c>"processItems.validate"</c>,
    /// or the runtime loop path <c>"processItems.0.validate"</c>.
    /// Numeric segments are treated as loop iteration indices and skipped when resolving
    /// against the template definition.
    /// </param>
    /// <returns>The matching <see cref="StepMetadata"/>, or <see langword="null"/> if not found.</returns>
    public StepMetadata? FindStep(string key)
    {
        if (TryGetValue(key, out var step))
        {
            return step;
        }

        // Support nested keys: "parent.child" and runtime loop keys: "parent.0.child".
        var segments = key.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length <= 1)
        {
            return null;
        }

        return FindStepRecursive(this, segments, 0, null);
    }

    private static StepMetadata? FindStepRecursive(
        IDictionary<string, StepMetadata> current,
        IReadOnlyList<string> segments,
        int index,
        StepMetadata? parentScopedStep)
    {
        if (index >= segments.Count)
        {
            return null;
        }

        // Runtime index segment for loop iterations (e.g. "parent.0.child").
        // If the previous segment points to a scoped step, skip the numeric segment.
        if (int.TryParse(segments[index], out _) && parentScopedStep is IScopedStep scopedParent)
        {
            if (index == segments.Count - 1)
            {
                // Key ends at runtime index "parent.0" -> return the loop metadata.
                return parentScopedStep;
            }

            return FindStepRecursive(scopedParent.Steps, segments, index + 1, parentScopedStep);
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
            return FindStepRecursive(scoped.Steps, segments, index + 1, step);
        }

        return null;
    }

    /// <summary>
    /// Returns the first top-level step whose <see cref="StepMetadata.RunAfter"/> references
    /// <paramref name="currentKey"/>, effectively finding the immediate successor in a linear chain.
    /// </summary>
    /// <param name="currentKey">The key of the step that just completed.</param>
    /// <returns>The next step, or <see langword="null"/> if none declares <paramref name="currentKey"/> as a dependency.</returns>
    public StepMetadata? FindNextStep(string currentKey)
    {
        foreach (var kvp in this)
        {
            var metadata = kvp.Value;
            if (ReferenceEquals(metadata, null))
            {
                continue;
            }

            if (metadata.RunAfter.TryGetValue(currentKey, out var condition)
                && condition?.Statuses is { Length: > 0 })
            {
                return metadata;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the scoped parent step (e.g. a <see cref="LoopStepMetadata"/>) that contains
    /// the step identified by <paramref name="key"/>.
    /// </summary>
    /// <param name="key">A dot-separated step path such as <c>"processItems.validate"</c>.</param>
    /// <returns>
    /// The parent <see cref="StepMetadata"/> if the key has more than one segment and the parent exists;
    /// otherwise <see langword="null"/>.
    /// </returns>
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
