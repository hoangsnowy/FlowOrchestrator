using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Storage;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Default <see cref="IRecurringTriggerSync"/> implementation that synchronises Hangfire
/// recurring jobs for a flow's cron triggers using the current schedule state and overrides.
/// </summary>
/// <remarks>
/// Uses synchronous <c>GetAwaiter().GetResult()</c> calls because Hangfire's
/// <c>IRecurringJobManager</c> API is synchronous — this method is only called from
/// the dashboard API, not from a hot path.
/// </remarks>
internal sealed class RecurringTriggerSync : IRecurringTriggerSync
{
    private readonly IFlowRepository _repository;
    private readonly IRecurringJobManager _recurringJobManager;
    private readonly IFlowScheduleStateStore _scheduleStateStore;
    private readonly FlowSchedulerOptions _schedulerOptions;
    private readonly ILogger<RecurringTriggerSync> _logger;

    public RecurringTriggerSync(
        IFlowRepository repository,
        IRecurringJobManager recurringJobManager,
        IFlowScheduleStateStore scheduleStateStore,
        FlowSchedulerOptions schedulerOptions,
        ILogger<RecurringTriggerSync> logger)
    {
        _repository = repository;
        _recurringJobManager = recurringJobManager;
        _scheduleStateStore = scheduleStateStore;
        _schedulerOptions = schedulerOptions;
        _logger = logger;
    }

    public void SyncTriggers(Guid flowId, bool isEnabled)
    {
        var flows = _repository.GetAllFlowsAsync().AsTask().GetAwaiter().GetResult();
        var flow = flows.FirstOrDefault(f => f.Id == flowId);
        if (flow is null) return;

        foreach (var (triggerKey, trigger) in flow.Manifest.Triggers)
        {
            var jobId = $"flow-{flow.Id}-{triggerKey}";
            var state = _schedulerOptions.PersistOverrides
                ? _scheduleStateStore.GetAsync(jobId).GetAwaiter().GetResult()
                : null;

            if (trigger.Type != TriggerType.Cron)
                continue;

            if (!isEnabled
                || !trigger.TryGetCronExpression(out var cronExpression))
            {
                _recurringJobManager.RemoveIfExists(jobId);
                _logger.LogInformation("Removed recurring job {JobId}.", jobId);
                continue;
            }

            if (state?.IsPaused == true)
            {
                _recurringJobManager.RemoveIfExists(jobId);
                _logger.LogInformation("Recurring job {JobId} remains paused.", jobId);
                continue;
            }

            var effectiveCron = string.IsNullOrWhiteSpace(state?.CronOverride)
                ? cronExpression
                : state!.CronOverride!;

            var fid = flow.Id;
            var key = triggerKey;
            _recurringJobManager.AddOrUpdate<IHangfireFlowTrigger>(
                jobId,
                t => t.TriggerByScheduleAsync(fid, key, null),
                effectiveCron);

            _logger.LogInformation("Registered recurring job {JobId} with cron '{Cron}'.", jobId, effectiveCron);
        }
    }
}
