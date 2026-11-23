using System.Collections.Concurrent;

namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Process-local <see cref="IFlowScheduleStateStore"/>: dashboard pause states and cron overrides
/// live for the lifetime of the process and are lost on restart.
/// </summary>
/// <remarks>
/// The single default used whenever no persistent store is registered — by <c>AddFlowOrchestrator</c>
/// (in-memory storage, or SQL / PostgreSQL storage with a custom runtime) and by
/// <c>AddFlowDashboard</c> when it is wired without <c>AddFlowOrchestrator</c>. The SQL Server and
/// PostgreSQL backends register their own durable implementations.
/// <para>
/// <see cref="SaveAsync"/> stores a copy, so a caller mutating the instance it saved — or one it read
/// back — cannot change the stored state behind the store's back.
/// </para>
/// </remarks>
public sealed class InMemoryFlowScheduleStateStore : IFlowScheduleStateStore
{
    private readonly ConcurrentDictionary<string, FlowScheduleState> _states = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates an empty store stamped with the system clock.</summary>
    public InMemoryFlowScheduleStateStore()
        : this(TimeProvider.System)
    {
    }

    /// <summary>Creates an empty store that stamps <see cref="FlowScheduleState.UpdatedAtUtc"/> from <paramref name="timeProvider"/>.</summary>
    /// <param name="timeProvider">Clock used for <see cref="FlowScheduleState.UpdatedAtUtc"/>.</param>
    public InMemoryFlowScheduleStateStore(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public Task<FlowScheduleState?> GetAsync(string jobId)
    {
        _states.TryGetValue(jobId, out var state);
        return Task.FromResult(state is null ? null : Copy(state, state.UpdatedAtUtc));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<FlowScheduleState>> GetAllAsync()
    {
        var states = new List<FlowScheduleState>(_states.Count);
        foreach (var state in _states.Values)
        {
            states.Add(Copy(state, state.UpdatedAtUtc));
        }

        states.Sort(static (a, b) => string.CompareOrdinal(a.JobId, b.JobId));
        return Task.FromResult<IReadOnlyList<FlowScheduleState>>(states);
    }

    /// <inheritdoc/>
    public Task SaveAsync(FlowScheduleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var copy = Copy(state, _timeProvider.GetUtcNow());
        _states[copy.JobId] = copy;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string jobId)
    {
        _states.TryRemove(jobId, out _);
        return Task.CompletedTask;
    }

    private static FlowScheduleState Copy(FlowScheduleState state, DateTimeOffset updatedAtUtc) => new()
    {
        JobId = state.JobId,
        FlowId = state.FlowId,
        FlowName = state.FlowName,
        TriggerKey = state.TriggerKey,
        IsPaused = state.IsPaused,
        CronOverride = state.CronOverride,
        UpdatedAtUtc = updatedAtUtc
    };
}
