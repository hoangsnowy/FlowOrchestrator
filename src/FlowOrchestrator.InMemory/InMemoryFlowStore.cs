using System.Collections.Concurrent;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.InMemory;

/// <summary>
/// In-memory implementation of <see cref="IFlowStore"/> that stores <see cref="FlowDefinitionRecord"/>
/// instances in a thread-safe dictionary. All state is lost on process restart.
/// </summary>
public sealed class InMemoryFlowStore : IFlowStore
{
    private readonly ConcurrentDictionary<Guid, FlowDefinitionRecord> _flows = new();

    public Task<IReadOnlyList<FlowDefinitionRecord>> GetAllAsync()
    {
        IReadOnlyList<FlowDefinitionRecord> result = _flows.Values
            .OrderBy(f => f.Name)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<FlowDefinitionRecord?> GetByIdAsync(Guid id)
    {
        _flows.TryGetValue(id, out var record);
        return Task.FromResult(record);
    }

    public Task<FlowDefinitionRecord> SaveAsync(FlowDefinitionRecord record)
    {
        record.UpdatedAt = DateTimeOffset.UtcNow;
        if (record.CreatedAt == default)
            record.CreatedAt = record.UpdatedAt;
        _flows[record.Id] = record;
        return Task.FromResult(record);
    }

    public Task DeleteAsync(Guid id)
    {
        _flows.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task<FlowDefinitionRecord> SetEnabledAsync(Guid id, bool enabled)
    {
        if (_flows.TryGetValue(id, out var record))
        {
            record.IsEnabled = enabled;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(record);
        }
        throw new KeyNotFoundException($"Flow {id} not found.");
    }
}
