namespace FlowOrchestrator.Core.Execution.Internal;

/// <summary>
/// One enclosing scope of a runtime step key: the scope's own runtime key and the iteration
/// index the key selects within it.
/// </summary>
/// <param name="ScopeKey">The enclosing scope's runtime key, e.g. <c>"outer.1.inner"</c>.</param>
/// <param name="Index">The zero-based iteration index selected within that scope.</param>
internal readonly record struct RuntimeScope(string ScopeKey, int Index);

/// <summary>
/// Parsing of runtime step keys of the form <c>"{scope}.{index}.{child}"</c>, which the engine
/// builds when a scoped step (<c>ForEach</c>) fans its body out per iteration.
/// </summary>
/// <remarks>
/// <para>
/// This is the single parser for that shape. Four independent copies had accumulated —
/// <c>LoopBarrier.EnclosingLoopKeys</c>, <c>StepOutputResolver.EnumerateRuntimeScopes</c>,
/// <c>FlowGraphPlanner.ExtractRuntimeScopePrefixes</c> and <c>LoopScopeInputs</c> — and they had
/// already drifted from one another in where they start scanning and which indices they accept.
/// A fix to loop-key parsing had to be made in four places to be complete.
/// </para>
/// <para>
/// Scanning starts at the last segment, not the second-to-last: <c>"{scope}.{index}"</c> is a
/// legitimate key — <see cref="Abstractions.StepCollection.FindStep"/> resolves it to the scope's
/// metadata — so a parser that assumes a trailing child name silently misses it.
/// </para>
/// </remarks>
internal static class RuntimeStepKey
{
    /// <summary>
    /// Yields every enclosing scope of <paramref name="runtimeStepKey"/>, innermost first.
    /// <c>"outer.1.inner.0.child"</c> yields <c>("outer.1.inner", 0)</c> then <c>("outer", 1)</c>.
    /// Yields nothing for a top-level key.
    /// </summary>
    /// <param name="runtimeStepKey">A runtime step key, possibly carrying iteration indices.</param>
    /// <remarks>
    /// Lazily evaluated: a caller that only needs the innermost scope stops after one segment scan.
    /// Negative indices are not treated as iteration segments — an index is always non-negative,
    /// and accepting <c>-1</c> would let a step key containing a negative number masquerade as a
    /// scope.
    /// </remarks>
    public static IEnumerable<RuntimeScope> EnumerateScopes(string? runtimeStepKey)
    {
        if (string.IsNullOrEmpty(runtimeStepKey) || !runtimeStepKey.Contains('.', StringComparison.Ordinal))
        {
            yield break;
        }

        var segments = runtimeStepKey.Split(
            '.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Start at the last segment: "{scope}.{index}" is a valid key with no trailing child.
        for (var i = segments.Length - 1; i >= 1; i--)
        {
            if (int.TryParse(segments[i], out var index) && index >= 0)
            {
                // string.Join's array-range overload — same output as Take(i), no LINQ iterator.
                yield return new RuntimeScope(string.Join('.', segments, 0, i), index);
            }
        }
    }

    /// <summary>
    /// Yields the runtime keys of the scopes enclosing <paramref name="runtimeStepKey"/>,
    /// innermost first, discarding the iteration indices.
    /// </summary>
    public static IReadOnlyList<string> EnclosingScopeKeys(string? runtimeStepKey)
    {
        List<string>? keys = null;
        foreach (var scope in EnumerateScopes(runtimeStepKey))
        {
            (keys ??= []).Add(scope.ScopeKey);
        }
        return keys ?? (IReadOnlyList<string>)[];
    }
}
