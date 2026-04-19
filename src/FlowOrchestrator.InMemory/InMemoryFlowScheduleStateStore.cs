using System.Collections.Concurrent;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.InMemory;

/// <summary>
/// Persistent in-memory implementation of <see cref="IFlowScheduleStateStore"/> registered when
/// <c>UseInMemory()</c> is configured. Schedule overrides and pause states survive for the lifetime
/// of the process but are lost on restart.
/// </summary>
public sealed class InMemoryFlowScheduleStateStore : IFlowScheduleStateStore
{
    private readonly ConcurrentDictionary<string, FlowScheduleState> _states = new(StringComparer.Ordinal);

    public Task<FlowScheduleState?> GetAsync(string jobId)
    {
        _states.TryGetValue(jobId, out var state);
        return Task.FromResult<FlowScheduleState?>(state);
    }

    public Task<IReadOnlyList<FlowScheduleState>> GetAllAsync()
    {
        IReadOnlyList<FlowScheduleState> items = _states.Values
            .OrderBy(x => x.JobId, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(items);
    }

    public Task SaveAsync(FlowScheduleState state)
    {
        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        _states[state.JobId] = state;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string jobId)
    {
        _states.TryRemove(jobId, out _);
        return Task.CompletedTask;
    }
}

