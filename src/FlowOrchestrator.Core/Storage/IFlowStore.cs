namespace FlowOrchestrator.Core.Storage;

public interface IFlowStore
{
    Task<IReadOnlyList<FlowDefinitionRecord>> GetAllAsync();
    Task<FlowDefinitionRecord?> GetByIdAsync(Guid id);
    Task<FlowDefinitionRecord> SaveAsync(FlowDefinitionRecord record);
    Task DeleteAsync(Guid id);
    Task<FlowDefinitionRecord> SetEnabledAsync(Guid id, bool enabled);
}
