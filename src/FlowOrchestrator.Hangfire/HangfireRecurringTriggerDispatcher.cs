using FlowOrchestrator.Core.Execution;
using Hangfire;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Hangfire implementation of <see cref="IRecurringTriggerDispatcher"/>.
/// Delegates cron-schedule management to <see cref="IRecurringJobManager"/>
/// and one-off enqueues to <see cref="IBackgroundJobClient"/>.
/// </summary>
internal sealed class HangfireRecurringTriggerDispatcher : IRecurringTriggerDispatcher
{
    private readonly IRecurringJobManager _manager;
    private readonly IBackgroundJobClient _client;

    /// <summary>Initialises the dispatcher with required Hangfire services.</summary>
    public HangfireRecurringTriggerDispatcher(IRecurringJobManager manager, IBackgroundJobClient client)
    {
        _manager = manager;
        _client = client;
    }

    /// <inheritdoc/>
    public void RegisterOrUpdate(string jobId, Guid flowId, string triggerKey, string cronExpression)
    {
        var fid = flowId;
        var key = triggerKey;
        _manager.AddOrUpdate<IHangfireFlowTrigger>(
            jobId,
            t => t.TriggerByScheduleAsync(fid, key, null),
            cronExpression);
    }

    /// <inheritdoc/>
    public void Remove(string jobId) => _manager.RemoveIfExists(jobId);

    /// <inheritdoc/>
    public void TriggerOnce(string jobId) => _manager.Trigger(jobId);

    /// <inheritdoc/>
    public Task EnqueueTriggerAsync(Guid flowId, string triggerKey, CancellationToken ct = default)
    {
        var fid = flowId;
        var key = triggerKey;
        _client.Enqueue<IHangfireFlowTrigger>(t => t.TriggerByScheduleAsync(fid, key, null));
        return Task.CompletedTask;
    }
}
