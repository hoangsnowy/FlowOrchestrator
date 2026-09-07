using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution.Internal;

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
    /// Returns <paramref name="inputs"/> augmented with the enclosing loop's
    /// <c>__loopItem</c> and <c>__loopIndex</c>, or the original dictionary unchanged when
    /// <paramref name="runtimeStepKey"/> is not a loop child or already carries both keys.
    /// </summary>
    /// <param name="inputs">The step's inputs, already passed through expression resolution.</param>
    /// <param name="runtimeStepKey">The step's runtime key, e.g. <c>"scan_process.2.open_camera"</c>.</param>
    /// <param name="steps">The flow manifest's step collection, used to locate the enclosing loop.</param>
    /// <param name="triggerData">The run's trigger payload, used to re-resolve the iteration source.</param>
    /// <param name="triggerHeaders">The run's trigger headers, used to re-resolve the iteration source.</param>
    /// <returns>
    /// The augmented dictionary, or <paramref name="inputs"/> itself when nothing had to be added —
    /// keeping the non-loop path (the overwhelming majority of steps) allocation-free.
    /// </returns>
    /// <remarks>
    /// Inputs that already carry <b>both</b> keys are left untouched, so the value the fan-out
    /// computed always wins over the recomputed one.
    /// </remarks>
    public static IDictionary<string, object?> Apply(
        IDictionary<string, object?> inputs,
        string runtimeStepKey,
        StepCollection steps,
        object? triggerData,
        IReadOnlyDictionary<string, string>? triggerHeaders)
    {
        if (inputs.ContainsKey(LoopItemKey) && inputs.ContainsKey(LoopIndexKey))
        {
            return inputs;
        }

        if (!TryParseInnermostScope(runtimeStepKey, out var loopKey, out var index))
        {
            return inputs;
        }

        if (steps.FindStep(loopKey) is not LoopStepMetadata loopMetadata)
        {
            return inputs;
        }

        var source = ForEachSourceResolver.Resolve(loopMetadata.ForEach, triggerData, triggerHeaders);

        var result = new Dictionary<string, object?>(inputs, StringComparer.Ordinal)
        {
            [LoopIndexKey] = index
        };

        // An unresolvable item still leaves __loopIndex set: a handler that only needs the index
        // (a positional lookup into its own store) keeps working even if the iteration source is
        // no longer materialisable.
        result[LoopItemKey] = ForEachSourceResolver.TryGetItemAt(source, index, out var item) ? item : null;

        return result;
    }

    /// <summary>
    /// Returns the innermost enclosing loop's zero-based iteration index for
    /// <paramref name="runtimeStepKey"/>, or <c>-1</c> when the key is not a loop child.
    /// </summary>
    /// <param name="runtimeStepKey">The step's runtime key, e.g. <c>"scan_process.2.open_camera"</c>.</param>
    /// <param name="steps">The flow manifest's step collection, used to confirm the scope is a real loop.</param>
    /// <remarks>
    /// Backs <see cref="IStepInstance{TInput}.Index"/>, whose contract is the iteration index of the
    /// enclosing <see cref="LoopStepMetadata"/> scope. No dispatch site ever assigned it, so it
    /// read <c>0</c> for every iteration; it is now set from the same runtime key that drives
    /// <see cref="Apply"/>, keeping it a true mirror of <c>__loopIndex</c>.
    /// </remarks>
    public static int GetIterationIndex(string runtimeStepKey, StepCollection steps)
    {
        if (!TryParseInnermostScope(runtimeStepKey, out var loopKey, out var index))
        {
            return -1;
        }

        return steps.FindStep(loopKey) is LoopStepMetadata ? index : -1;
    }

    /// <summary>
    /// Splits a runtime step key into its innermost enclosing loop scope and iteration index.
    /// For <c>"outer.0.inner.1.child"</c> this yields <c>("outer.0.inner", 1)</c>.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the key carries no iteration segment — i.e. it is a plain
    /// top-level step, not a loop child.
    /// </returns>
    private static bool TryParseInnermostScope(string runtimeStepKey, out string loopKey, out int index)
    {
        loopKey = string.Empty;
        index = -1;

        if (string.IsNullOrEmpty(runtimeStepKey) || !runtimeStepKey.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = runtimeStepKey.Split(
            '.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Start at Length - 2: the last segment is the child's own name, never an iteration index.
        for (var i = segments.Length - 2; i >= 1; i--)
        {
            if (!int.TryParse(segments[i], out var parsed) || parsed < 0)
            {
                continue;
            }

            loopKey = string.Join('.', segments, 0, i); // array-range overload — no LINQ Take iterator
            index = parsed;
            return true;
        }

        return false;
    }
}
