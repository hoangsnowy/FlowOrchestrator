namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Read-only interface for retrieving the ordered event log of a flow run.
/// Implemented alongside <see cref="IOutputsRepository"/> by storage backends that support event persistence.
/// </summary>
public interface IFlowEventReader
{
    /// <summary>
    /// Returns a page of events for the given run, ordered by <see cref="FlowEventRecord.Sequence"/> ascending.
    /// </summary>
    /// <param name="runId">The run whose events are requested.</param>
    /// <param name="skip">Number of events to skip (for pagination).</param>
    /// <param name="take">Maximum number of events to return.</param>
    Task<IReadOnlyList<FlowEventRecord>> GetRunEventsAsync(Guid runId, int skip = 0, int take = 200);
}
