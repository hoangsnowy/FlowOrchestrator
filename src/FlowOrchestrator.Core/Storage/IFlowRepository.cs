using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Read-only interface for retrieving code-registered flow definitions.
/// Implemented by <c>InMemoryFlowRepository</c>, which is populated at startup by
/// <c>FlowSyncHostedService</c> from the flows registered via <c>AddFlow&lt;T&gt;()</c>.
/// </summary>
public interface IFlowRepository
{
    /// <summary>Returns all <see cref="IFlowDefinition"/> instances registered in DI.</summary>
    ValueTask<IReadOnlyList<IFlowDefinition>> GetAllFlowsAsync();
}

/// <summary>
/// Helper for the dispatch-hot-path "find a flow by id" pattern.
/// </summary>
/// <remarks>
/// Replaces <c>flows.FirstOrDefault(f =&gt; f.Id == flowId)</c> at every dispatch
/// call site. The LINQ form allocates a closure capturing <c>flowId</c> on every
/// call; the for-loop form does not. Visible from a Stopwatch on tight dispatch
/// loops because <see cref="FlowOrchestratorEngine"/> hits this path on every
/// step run and every retry, and <c>HangfireFlowOrchestrator</c> hits it on every
/// dispatched job.
/// </remarks>
public static class FlowRepositoryExtensions
{
    /// <summary>
    /// Returns the flow with the given <paramref name="flowId"/> by linear scan,
    /// or <see langword="null"/> when not found. Allocation-free.
    /// </summary>
    public static IFlowDefinition? FindById(this IReadOnlyList<IFlowDefinition> flows, Guid flowId)
    {
        for (var i = 0; i < flows.Count; i++)
        {
            if (flows[i].Id == flowId)
            {
                return flows[i];
            }
        }
        return null;
    }
}
