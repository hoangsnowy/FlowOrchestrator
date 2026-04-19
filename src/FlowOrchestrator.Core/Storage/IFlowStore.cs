namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Persistence contract for flow definitions.
/// Implementations are provided by <c>UseSqlServer</c>, <c>UsePostgreSql</c>, and <c>UseInMemory</c>.
/// </summary>
public interface IFlowStore
{
    /// <summary>Returns all registered flow definitions, ordered by name.</summary>
    Task<IReadOnlyList<FlowDefinitionRecord>> GetAllAsync();

    /// <summary>
    /// Returns the flow definition with the given <paramref name="id"/>,
    /// or <see langword="null"/> if not found.
    /// </summary>
    Task<FlowDefinitionRecord?> GetByIdAsync(Guid id);

    /// <summary>
    /// Inserts or updates the flow definition record (upsert by <see cref="FlowDefinitionRecord.Id"/>).
    /// </summary>
    /// <returns>The persisted record, including any server-set timestamps.</returns>
    Task<FlowDefinitionRecord> SaveAsync(FlowDefinitionRecord record);

    /// <summary>Permanently deletes the flow definition with the given <paramref name="id"/>.</summary>
    Task DeleteAsync(Guid id);

    /// <summary>
    /// Enables or disables a flow without deleting it. Disabled flows are not triggered by
    /// the scheduler and are hidden from the active flow list.
    /// </summary>
    /// <returns>The updated record.</returns>
    Task<FlowDefinitionRecord> SetEnabledAsync(Guid id, bool enabled);
}
