using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// The single runtime-agnostic <see cref="IRecurringTriggerSync"/>: resolves each cron trigger's
/// effective schedule (enabled flag, persisted pause state, persisted cron override, manifest cron)
/// and registers or removes the job through the runtime's <see cref="IRecurringTriggerDispatcher"/>.
/// </summary>
/// <remarks>
/// Hangfire, the in-memory runtime and Service Bus used to carry three copies of this logic, each
/// blocking on the async schedule-state store with <c>GetAwaiter().GetResult()</c>. It now runs once,
/// asynchronously, and each runtime only owns the mechanics of a recurring job.
/// <para>Decision table per cron trigger:</para>
/// <list type="bullet">
///   <item>flow disabled, or the trigger has no cron expression → <see cref="IRecurringTriggerDispatcher.Remove"/>;</item>
///   <item>persisted state is paused (only consulted when <see cref="FlowSchedulerOptions.PersistOverrides"/>) → remove;</item>
///   <item>otherwise → <see cref="IRecurringTriggerDispatcher.RegisterOrUpdate"/> with the persisted
///         <see cref="FlowScheduleState.CronOverride"/> when set, else the manifest cron.</item>
/// </list>
/// </remarks>
public sealed class RecurringTriggerSync : IRecurringTriggerSync
{
    private readonly FlowRegistry _flows;
    private readonly IRecurringTriggerDispatcher _dispatcher;
    private readonly IFlowScheduleStateStore _scheduleStateStore;
    private readonly FlowSchedulerOptions _schedulerOptions;
    private readonly ILogger<RecurringTriggerSync> _logger;

    /// <summary>Initialises the sync with the flow registry, the runtime's dispatcher and the schedule-state store.</summary>
    /// <param name="flows">Registry used to resolve the flow's manifest triggers.</param>
    /// <param name="dispatcher">The configured runtime's recurring-job scheduler.</param>
    /// <param name="scheduleStateStore">Source of dashboard pause states and cron overrides.</param>
    /// <param name="schedulerOptions">Controls whether persisted overrides are honoured.</param>
    /// <param name="logger">Optional logger; defaults to a no-op logger.</param>
    public RecurringTriggerSync(
        FlowRegistry flows,
        IRecurringTriggerDispatcher dispatcher,
        IFlowScheduleStateStore scheduleStateStore,
        FlowSchedulerOptions schedulerOptions,
        ILogger<RecurringTriggerSync>? logger = null)
    {
        _flows = flows;
        _dispatcher = dispatcher;
        _scheduleStateStore = scheduleStateStore;
        _schedulerOptions = schedulerOptions;
        _logger = logger ?? NullLogger<RecurringTriggerSync>.Instance;
    }

    /// <inheritdoc/>
    /// <remarks>An unregistered <paramref name="flowId"/> is a no-op.</remarks>
    public async Task SyncTriggersAsync(Guid flowId, bool isEnabled, CancellationToken cancellationToken = default)
    {
        if (!_flows.TryGetFlow(flowId, out var flow))
        {
            return;
        }

        foreach (var (triggerKey, trigger) in flow.Manifest.Triggers)
        {
            if (trigger.Type != TriggerType.Cron)
            {
                continue;
            }

            var jobId = RecurringTriggerJobId.Create(flow.Id, triggerKey);

            if (!isEnabled || !trigger.TryGetCronExpression(out var manifestCron))
            {
                _dispatcher.Remove(jobId);
                HostingLog.RecurringJobRemoved(_logger, jobId);
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var persisted = _schedulerOptions.PersistOverrides
                ? await _scheduleStateStore.GetAsync(jobId).ConfigureAwait(false)
                : null;

            if (persisted?.IsPaused == true)
            {
                _dispatcher.Remove(jobId);
                HostingLog.RecurringJobPaused(_logger, jobId);
                continue;
            }

            var effectiveCron = string.IsNullOrWhiteSpace(persisted?.CronOverride)
                ? manifestCron
                : persisted.CronOverride;

            _dispatcher.RegisterOrUpdate(jobId, flow.Id, triggerKey, effectiveCron);
            HostingLog.RecurringJobSynced(_logger, jobId, effectiveCron);
        }
    }
}
