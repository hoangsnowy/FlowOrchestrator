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
    /// <remarks>
    /// Hot path: called per status-map entry by <c>LoopBarrier.RunningLoopKeys</c> and per ready step by
    /// the continuation, i.e. on every step completion. Nested and runtime keys are walked as
    /// <see cref="ReadOnlySpan{T}"/> segments — no <see cref="string.Split(char, StringSplitOptions)"/>
    /// array and, on .NET 9+, no per-segment string (the lookup goes through
    /// <c>GetAlternateLookup&lt;ReadOnlySpan&lt;char&gt;&gt;</c>). Segment semantics are unchanged from the
    /// earlier <c>Split(RemoveEmptyEntries | TrimEntries)</c> form: segments are trimmed, empty ones are
    /// ignored, and a key with fewer than two segments that is not an exact top-level key resolves to
    /// <see langword="null"/> (#189).
    /// </remarks>
    public StepMetadata? FindStep(string key)
    {
        if (TryGetValue(key, out var step))
        {
            return step;
        }

        // Support nested keys: "parent.child" and runtime loop keys: "parent.0.child".
        var segmentCount = CountSegments(key);
        if (segmentCount <= 1)
        {
            return null;
        }

        IDictionary<string, StepMetadata> current = this;
        StepMetadata? parentScopedStep = null;
        var rest = key.AsSpan();
        for (var index = 0; index < segmentCount; index++)
        {
            var segment = NextSegment(ref rest);
            var isLast = index == segmentCount - 1;

            // Runtime index segment for loop iterations (e.g. "parent.0.child").
            // If the previous segment points to a scoped step, skip the numeric segment.
            if (parentScopedStep is IScopedStep scopedParent && int.TryParse(segment, out _))
            {
                if (isLast)
                {
                    // Key ends at runtime index "parent.0" -> return the loop metadata.
                    return parentScopedStep;
                }

                current = scopedParent.Steps;
                continue;
            }

            if (!TryGetSegment(current, segment, out var found) || found is null)
            {
                return null;
            }

            if (isLast)
            {
                return found;
            }

            if (found is not IScopedStep scoped || scoped.Steps is not { Count: > 0 })
            {
                return null;
            }

            current = scoped.Steps;
            parentScopedStep = found;
        }

        return null;
    }

    /// <summary>Counts the trimmed, non-empty dot-separated segments of <paramref name="key"/>.</summary>
    private static int CountSegments(ReadOnlySpan<char> key)
    {
        var count = 0;
        while (!key.IsEmpty)
        {
            if (!NextSegment(ref key).IsEmpty)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Returns the next trimmed, non-empty segment of <paramref name="rest"/> and advances past it,
    /// or an empty span when only separators and whitespace remain.
    /// </summary>
    private static ReadOnlySpan<char> NextSegment(ref ReadOnlySpan<char> rest)
    {
        while (!rest.IsEmpty)
        {
            var dot = rest.IndexOf('.');
            var segment = (dot < 0 ? rest : rest[..dot]).Trim();
            rest = dot < 0 ? ReadOnlySpan<char>.Empty : rest[(dot + 1)..];
            if (!segment.IsEmpty)
            {
                return segment;
            }
        }

        return ReadOnlySpan<char>.Empty;
    }

    private static bool TryGetSegment(IDictionary<string, StepMetadata> steps, ReadOnlySpan<char> segment, out StepMetadata? step)
    {
#if NET9_0_OR_GREATER
        if (steps is Dictionary<string, StepMetadata> dictionary
            && dictionary.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup))
        {
            return lookup.TryGetValue(segment, out step);
        }
#endif
        return steps.TryGetValue(segment.ToString(), out step);
    }

    /// <summary>
    /// Returns the first top-level step whose <see cref="StepMetadata.RunAfter"/> references
    /// <paramref name="currentKey"/>, effectively finding the immediate successor in a linear chain.
    /// </summary>
    /// <param name="currentKey">The key of the step that just completed.</param>
    /// <returns>The next step, or <see langword="null"/> if none declares <paramref name="currentKey"/> as a dependency.</returns>
    public StepMetadata? FindNextStep(string currentKey)
    {
        return this.Values
            .Where(metadata => !ReferenceEquals(metadata, null))
            .FirstOrDefault(metadata =>
                metadata.RunAfter.TryGetValue(currentKey, out var condition)
                && condition?.Statuses is { Length: > 0 });
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
