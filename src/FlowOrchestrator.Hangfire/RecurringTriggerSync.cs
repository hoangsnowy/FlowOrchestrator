using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Hangfire;

internal sealed class RecurringTriggerSync : IRecurringTriggerSync
{
    private readonly IFlowRepository _repository;
    private readonly IRecurringJobManager _recurringJobManager;
    private readonly ILogger<RecurringTriggerSync> _logger;

    public RecurringTriggerSync(
        IFlowRepository repository,
        IRecurringJobManager recurringJobManager,
        ILogger<RecurringTriggerSync> logger)
    {
        _repository = repository;
        _recurringJobManager = recurringJobManager;
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

            if (trigger.Type != TriggerType.Cron)
                continue;

            if (!isEnabled
                || !trigger.TryGetCronExpression(out var cronExpression))
            {
                _recurringJobManager.RemoveIfExists(jobId);
                _logger.LogInformation("Removed recurring job {JobId}.", jobId);
                continue;
            }

            var fid = flow.Id;
            var key = triggerKey;
            _recurringJobManager.AddOrUpdate<IHangfireFlowTrigger>(
                jobId,
                t => t.TriggerByScheduleAsync(fid, key, null),
                cronExpression);

            _logger.LogInformation("Registered recurring job {JobId} with cron '{Cron}'.", jobId, cronExpression);
        }
    }
}
