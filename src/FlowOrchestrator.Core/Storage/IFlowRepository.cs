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
