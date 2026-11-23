using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace FlowOrchestrator.Core.Abstractions;

/// <summary>
/// Immutable, process-wide registry of the code-defined <see cref="IFlowDefinition"/> instances
/// registered through <c>AddFlow&lt;TFlow&gt;()</c>. Built once at container resolution and read on
/// every step execution, retry, signal delivery and cron fire.
/// </summary>
/// <remarks>
/// Replaces the 1.x <c>IFlowRepository</c> interface and its three identical per-backend copies.
/// Lookups by id are O(1) against a <see cref="FrozenDictionary{TKey,TValue}"/>; the old
/// interface forced every hot-path caller to fetch the whole list and scan it linearly.
/// <para>
/// Registering the same flow type twice (e.g. calling <c>AddFlow&lt;TFlow&gt;()</c> twice) is harmless:
/// the first registration is kept. Two <i>different</i> flow types that share an
/// <see cref="IFlowDefinition.Id"/> are a configuration error — runs, schedules and step jobs are keyed by
/// that id, so one flow would silently shadow the other — and fail at container resolution.
/// </para>
/// </remarks>
public sealed class FlowRegistry
{
    private readonly FrozenDictionary<Guid, IFlowDefinition> _byId;

    /// <summary>A registry with no flows; useful for hosts and tests that register none.</summary>
    public static FlowRegistry Empty { get; } = new([]);

    /// <summary>Builds the registry from the registered flow definitions.</summary>
    /// <param name="flows">
    /// The flows to index, in registration order. A repeated registration of the same flow type keeps
    /// the first occurrence.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="flows"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Two different flow types share an id.</exception>
    public FlowRegistry(IEnumerable<IFlowDefinition> flows)
    {
        ArgumentNullException.ThrowIfNull(flows);

        var byId = new Dictionary<Guid, IFlowDefinition>();
        var ordered = new List<IFlowDefinition>();
        foreach (var flow in flows)
        {
            if (byId.TryAdd(flow.Id, flow))
            {
                ordered.Add(flow);
                continue;
            }

            var existing = byId[flow.Id];
            if (existing.GetType() != flow.GetType())
            {
                throw new InvalidOperationException(
                    $"Flows '{existing.GetType().FullName}' and '{flow.GetType().FullName}' are both registered with id " +
                    $"'{flow.Id}'. Flow ids must be unique: runs, schedules and step jobs are keyed by them.");
            }
        }

        _byId = byId.ToFrozenDictionary();
        Flows = ordered.AsReadOnly();
    }

    /// <summary>Every registered flow, in registration order, one entry per distinct id.</summary>
    public IReadOnlyList<IFlowDefinition> Flows { get; }

    /// <summary>Looks up the flow registered under <paramref name="flowId"/> in O(1).</summary>
    /// <param name="flowId">The flow's stable identifier.</param>
    /// <param name="flow">The registered definition when found; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a flow with that id is registered.</returns>
    public bool TryGetFlow(Guid flowId, [NotNullWhen(true)] out IFlowDefinition? flow)
        => _byId.TryGetValue(flowId, out flow);

    /// <summary>
    /// Returns the flow registered under <paramref name="flowId"/>, or <see langword="null"/>
    /// when no such flow is registered.
    /// </summary>
    /// <param name="flowId">The flow's stable identifier.</param>
    public IFlowDefinition? Find(Guid flowId)
        => _byId.TryGetValue(flowId, out var flow) ? flow : null;
}
