using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution.Internal;

/// <summary>
/// The reverse of a flow's <c>RunAfter</c> graph — for every step, the steps that depend on it — built once
/// per flow definition so the engine can ask "which steps can a change to <c>X</c> have made ready?" without
/// reading the run's whole status map (#189 P0-1).
/// </summary>
/// <remarks>
/// <para>
/// A step's classification (ready, blocked, waiting) is a function of its own status row and its
/// dependencies' rows only. So when step <c>X</c> reaches a status, the only steps whose classification can
/// change are <c>D(X)</c>, the steps that list <c>X</c> in their <c>RunAfter</c>; reading the statuses of
/// <c>D(X)</c> and of their dependencies is enough to classify them. That is the frontier read of RFC 2.0
/// §3.5.
/// </para>
/// <para>
/// Dependencies resolve the way <see cref="FlowGraphPlanner"/> resolves them: a key without a dot is a sibling
/// in the dependent's own scope instance (<c>"loop.3.b"</c> depending on <c>"a"</c> reads <c>"loop.3.a"</c>),
/// and a dotted key is taken literally. Siblings are indexed per <see cref="StepCollection"/>, so the runtime
/// dependents of <c>"loop.3.a"</c> are found by one lookup in the loop body's table plus a prefix join.
/// A literal dotted dependency declared by a step nested in a scope has a dependent in every scope instance;
/// those cannot be enumerated by key, and <see cref="TryAddDependents"/> reports them so the caller falls
/// back to a full evaluation.
/// </para>
/// <para>
/// Manifests are immutable after startup — the same assumption <see cref="FlowGraphPlanner"/>'s key cache
/// makes — so the tables are frozen and keyed on the definition instance through a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/>, which also lets an unregistered flow be collected.
/// </para>
/// </remarks>
internal sealed class FlowTopology
{
    private static readonly ConditionalWeakTable<IFlowDefinition, FlowTopology> Cache = new();

    /// <summary>Per scope (by collection instance): child key → keys of the siblings that depend on it.</summary>
    private readonly Dictionary<StepCollection, FrozenDictionary<string, string[]>> _siblingDependents;

    /// <summary>Literal dotted dependency key → the dependents declaring it.</summary>
    private readonly FrozenDictionary<string, AbsoluteDependent[]> _absoluteDependents;

    private FlowTopology(StepCollection topLevel)
    {
        _siblingDependents = new Dictionary<StepCollection, FrozenDictionary<string, string[]>>(ReferenceEqualityComparer.Instance);
        var absolute = new Dictionary<string, List<AbsoluteDependent>>(StringComparer.Ordinal);
        Index(topLevel, templatePrefix: null, absolute);
        _absoluteDependents = absolute.ToFrozenDictionary(
            static pair => pair.Key, static pair => pair.Value.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>Returns the topology of <paramref name="flow"/>, building it on first use.</summary>
    /// <param name="flow">A registered flow definition.</param>
    /// <returns>The cached topology; O(1) after the first call for a definition.</returns>
    public static FlowTopology For(IFlowDefinition flow)
        => Cache.GetValue(flow, static f => new FlowTopology(f.Manifest.Steps));

    /// <summary>
    /// Appends the runtime keys of the steps that depend on <paramref name="runtimeStepKey"/> to
    /// <paramref name="dependents"/>.
    /// </summary>
    /// <param name="topLevel">The flow's top-level steps.</param>
    /// <param name="runtimeStepKey">The step whose status just changed, e.g. <c>"loop.3.a"</c>.</param>
    /// <param name="dependents">Receives the dependents' runtime keys; may receive duplicates.</param>
    /// <returns>
    /// <see langword="false"/> when the dependents cannot be enumerated by key — the key does not resolve to
    /// a step, or a nested step depends on it through a literal dotted key — and the caller must evaluate
    /// the run's whole graph instead.
    /// </returns>
    /// <remarks>
    /// <b>Complexity:</b> O(key length) to resolve the scope, plus O(1) per dependent. Independent of the
    /// number of status rows in the run.
    /// </remarks>
    public bool TryAddDependents(StepCollection topLevel, string runtimeStepKey, ICollection<string> dependents)
    {
        var lastDot = runtimeStepKey.LastIndexOf('.');
        StepCollection scope;
        string? runtimeParent;
        string childKey;
        if (lastDot < 0)
        {
            scope = topLevel;
            runtimeParent = null;
            childKey = runtimeStepKey;
            if (!topLevel.ContainsKey(childKey))
            {
                return false;
            }
        }
        else
        {
            runtimeParent = runtimeStepKey[..lastDot];
            if (topLevel.FindStep(runtimeParent) is not IScopedStep { Steps: { Count: > 0 } children })
            {
                return false;
            }

            scope = children;
            childKey = runtimeStepKey[(lastDot + 1)..];
        }

        if (_siblingDependents.TryGetValue(scope, out var siblings)
            && siblings.TryGetValue(childKey, out var siblingKeys))
        {
            foreach (var sibling in siblingKeys)
            {
                dependents.Add(runtimeParent is null ? sibling : string.Concat(runtimeParent, ".", sibling));
            }
        }

        if (_absoluteDependents.TryGetValue(runtimeStepKey, out var absolute))
        {
            foreach (var dependent in absolute)
            {
                if (!dependent.IsTopLevel)
                {
                    return false;
                }

                dependents.Add(dependent.Key);
            }
        }

        return true;
    }

    private void Index(StepCollection scope, string? templatePrefix, Dictionary<string, List<AbsoluteDependent>> absolute)
    {
        var siblings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (stepKey, metadata) in scope)
        {
            foreach (var dependency in metadata.RunAfter.Keys)
            {
                // The synthetic "" entry attaches a When clause to the trigger and names no step; a blank key
                // never matches a step key either (the planner looks it up verbatim and never finds it).
                if (string.IsNullOrWhiteSpace(dependency))
                {
                    continue;
                }

                if (dependency.Contains('.', StringComparison.Ordinal))
                {
                    if (!absolute.TryGetValue(dependency, out var list))
                    {
                        absolute[dependency] = list = [];
                    }

                    list.Add(new AbsoluteDependent(templatePrefix is null ? stepKey : $"{templatePrefix}.{stepKey}", templatePrefix is null));
                    continue;
                }

                if (!siblings.TryGetValue(dependency, out var dependentsOfDependency))
                {
                    siblings[dependency] = dependentsOfDependency = [];
                }

                dependentsOfDependency.Add(stepKey);
            }

            if (metadata is IScopedStep { Steps: { Count: > 0 } children })
            {
                Index(children, templatePrefix is null ? stepKey : $"{templatePrefix}.{stepKey}", absolute);
            }
        }

        _siblingDependents[scope] = siblings.ToFrozenDictionary(
            static pair => pair.Key, static pair => pair.Value.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>A step that names its dependency by a literal dotted key.</summary>
    /// <param name="Key">The dependent's key; a runtime key when <paramref name="IsTopLevel"/>.</param>
    /// <param name="IsTopLevel">Whether the dependent is a top-level step, i.e. has exactly one instance per run.</param>
    private readonly record struct AbsoluteDependent(string Key, bool IsTopLevel);
}
