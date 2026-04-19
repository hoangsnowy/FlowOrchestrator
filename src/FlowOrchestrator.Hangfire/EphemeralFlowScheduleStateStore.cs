using System.Collections.Concurrent;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// In-process, non-persistent implementation of <see cref="IFlowScheduleStateStore"/> used when
/// <c>PersistOverrides</c> is disabled. Schedule overrides and pause states are lost on process restart.
/// </summary>
internal sealed class EphemeralFlowScheduleStateStore : IFlowScheduleStateStore
{
    private readonly ConcurrentDictionary<string, FlowScheduleState> _states = new(StringComparer.Ordinal);

    public Task<FlowScheduleState?> GetAsync(string jobId)
    {
        _states.TryGetValue(jobId, out var state);
        return Task.FromResult<FlowScheduleState?>(state);
    }

    public Task<IReadOnlyList<FlowScheduleState>> GetAllAsync()
    {
        IReadOnlyList<FlowScheduleState> states = _states.Values
            .OrderBy(x => x.JobId, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(states);
    }

    public Task SaveAsync(FlowScheduleState state)
    {
        var copy = new FlowScheduleState
        {
            JobId = state.JobId,
            FlowId = state.FlowId,
            FlowName = state.FlowName,
            TriggerKey = state.TriggerKey,
            IsPaused = state.IsPaused,
            CronOverride = state.CronOverride,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        _states[copy.JobId] = copy;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string jobId)
    {
        _states.TryRemove(jobId, out _);
        return Task.CompletedTask;
    }
}

