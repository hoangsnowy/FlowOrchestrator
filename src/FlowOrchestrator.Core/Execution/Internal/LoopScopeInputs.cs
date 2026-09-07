using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution.Internal;

/// <summary>
/// The enclosing scope a runtime step key resolves to: the scope's metadata, its runtime key,
/// and the iteration index selected within it.
/// </summary>
/// <param name="Metadata">The scope's manifest metadata.</param>
/// <param name="ScopeKey">The scope's runtime key, e.g. <c>"scan_process"</c> or <c>"outer.1.inner"</c>.</param>
/// <param name="Index">The zero-based iteration index this step belongs to.</param>
internal readonly record struct LoopScope(IScopedStep Metadata, string ScopeKey, int Index);

/// <summary>
/// Derives the <c>__loopItem</c> / <c>__loopIndex</c> iteration context of a <c>ForEach</c>
/// child from its runtime step key and injects it into the step's inputs.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ForEachStepHandler"/> bakes the iteration context into the inputs of the children
/// it fans out — but it only fans out the loop body's <i>entry</i> steps. Every other child (any
/// step declaring a <c>RunAfter</c> inside the loop body) is dispatched later by the DAG
/// continuation, by signal resume, by an explicit retry, or by crash recovery, and all four build
/// their inputs from the loop's <b>template</b> metadata — which carries no iteration context at
/// all. The same holds for an entry child that is re-dispatched after a
/// <see cref="StepStatus.Pending"/> poll or a signal wait: its second dispatch also starts from
/// the template.
/// </para>
/// <para>
/// Rather than patch each dispatch site, the context is recomputed at execution time from the
/// runtime key (<c>"{loop}.{index}.{child}"</c>), which every loop child carries by construction.
/// The iteration source is re-resolved from the run's trigger payload — immutable for the life of
/// the run — so the value is identical to the one the fan-out computed.
/// </para>
/// </remarks>
internal static class LoopScopeInputs
{
    /// <summary>Input key carrying the current iteration's item.</summary>
    internal const string LoopItemKey = "__loopItem";

    /// <summary>Input key carrying the current iteration's zero-based index.</summary>
    internal const string LoopIndexKey = "__loopIndex";

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="key"/> is one of the engine-injected
    /// loop-context keys, whose values are iteration <i>data</i> and must never be treated as
    /// manifest expressions.
    /// </summary>
    internal static bool IsReservedKey(string key)
        => string.Equals(key, LoopItemKey, StringComparison.Ordinal)
        || string.Equals(key, LoopIndexKey, StringComparison.Ordinal);

    /// <summary>
    /// Resolves the innermost enclosing scope of <paramref name="runtimeStepKey"/> that exists in
    /// the manifest, or returns <see langword="false"/> when the key is not a scoped step's child.
    /// </summary>
    /// <param name="runtimeStepKey">The step's runtime key, e.g. <c>"scan_process.2.open_camera"</c>.</param>
    /// <param name="steps">The flow manifest's step collection.</param>
    /// <param name="scope">The resolved scope on success.</param>
    /// <remarks>
    /// Scopes are tried innermost-first and the first one that resolves to an
    /// <see cref="IScopedStep"/> wins, so a key whose innermost numeric segment does not name a
    /// scope degrades to the next scope out rather than losing its iteration context entirely.
    /// Matching on <see cref="IScopedStep"/> — not on <see cref="LoopStepMetadata"/> — keeps this
    /// consistent with every other scope-aware site in the engine (the barrier, skip tracking, key
    /// resolution), so a second scope kind inherits the behaviour instead of silently opting out.
    /// </remarks>
    public static bool TryResolveScope(string runtimeStepKey, StepCollection steps, out LoopScope scope)
    {
        foreach (var candidate in RuntimeStepKey.EnumerateScopes(runtimeStepKey))
        {
            if (steps.FindStep(candidate.ScopeKey) is IScopedStep scoped)
            {
                scope = new LoopScope(scoped, candidate.ScopeKey, candidate.Index);
                return true;
            }
        }

        scope = default;
        return false;
    }

    /// <summary>
    /// Returns <paramref name="inputs"/> augmented with the enclosing loop's <c>__loopItem</c> and
    /// <c>__loopIndex</c>, or the original dictionary unchanged when both are already present.
    /// </summary>
    /// <param name="inputs">The step's inputs, already passed through expression resolution.</param>
    /// <param name="scope">The scope resolved by <see cref="TryResolveScope"/>.</param>
    /// <param name="triggerData">The run's trigger payload, used to re-resolve the iteration source.</param>
    /// <param name="triggerHeaders">The run's trigger headers, used to re-resolve the iteration source.</param>
    /// <remarks>
    /// <para>
    /// Each key is guarded independently. An all-or-nothing guard would recompute a
    /// <c>__loopItem</c> that is already present whenever <c>__loopIndex</c> alone went missing,
    /// and overwrite a perfectly good item with <see langword="null"/> if the source could no
    /// longer be materialised — turning a partial loss into a total one.
    /// </para>
    /// <para>
    /// The copy preserves the source dictionary's comparer. A dispatcher or storage adapter that
    /// hands the engine case-insensitive inputs would otherwise have that silently downgraded to
    /// ordinal on loop children only, producing a <see cref="KeyNotFoundException"/> that
    /// reproduces on exactly one step type.
    /// </para>
    /// </remarks>
    public static IDictionary<string, object?> Apply(
        IDictionary<string, object?> inputs,
        in LoopScope scope,
        object? triggerData,
        IReadOnlyDictionary<string, string>? triggerHeaders)
    {
        var needsIndex = !inputs.ContainsKey(LoopIndexKey);

        // Only a MISSING item is recomputed. An item already present — whatever supplied it — is
        // authoritative, because the recompute can legitimately fail (a source that is no longer
        // materialisable) and must never downgrade a real value to null.
        var needsItem = !inputs.TryGetValue(LoopItemKey, out var existingItem) || existingItem is null;

        if (!needsIndex && !needsItem)
        {
            return inputs;
        }

        var result = new Dictionary<string, object?>(inputs, ComparerOf(inputs));

        if (needsIndex)
        {
            result[LoopIndexKey] = scope.Index;
        }

        if (needsItem && scope.Metadata is LoopStepMetadata loop)
        {
            var source = ForEachSourceResolver.Resolve(loop.ForEach, triggerData, triggerHeaders);

            // An unresolvable item still leaves __loopIndex set: a handler that only needs the
            // index (a positional lookup into its own store) keeps working even when the iteration
            // source is no longer materialisable.
            if (ForEachSourceResolver.TryGetItemAt(source, scope.Index, out var item))
            {
                result[LoopItemKey] = item;
            }
            else
            {
                result.TryAdd(LoopItemKey, null);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the comparer <paramref name="inputs"/> was built with, defaulting to
    /// <see cref="StringComparer.Ordinal"/> for implementations that do not expose one.
    /// </summary>
    private static IEqualityComparer<string> ComparerOf(IDictionary<string, object?> inputs)
        => inputs is Dictionary<string, object?> dictionary ? dictionary.Comparer : StringComparer.Ordinal;
}
