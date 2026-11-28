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
///         <see cref="FlowScheduleState.CronOverride"/> when set, else the manifest cron. An override the
///         runtime rejects as unparseable is logged (EventId 5011) and the manifest cron is registered
///         instead, so a bad persisted value can never abort host startup.</item>
/// </list>
/// </remarks>
internal sealed class RecurringTriggerSync : IRecurringTriggerSync
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

            var effectiveCron = RegisterWithFallback(jobId, flow.Id, triggerKey, manifestCron, persisted?.CronOverride);

            // A dashboard-persisted override is user input: strip line breaks before it reaches the log.
            // Cold path (startup and schedule edits only), so the chained Replace is fine here.
            HostingLog.RecurringJobSynced(_logger, jobId, Sanitize(effectiveCron));
        }
    }

    /// <summary>
    /// Registers the job on the persisted override when there is one, falling back to the manifest cron when
    /// the runtime rejects the override.
    /// </summary>
    /// <returns>The cron expression the job was actually registered with.</returns>
    /// <remarks>
    /// A persisted override is user input that an older version (or a direct write to the schedule-state
    /// table) can have stored unvalidated. Rethrowing the runtime's rejection here used to abort
    /// <c>FlowSyncHostedService</c> and therefore host startup, on every restart, until someone edited the
    /// table by hand. Only a cron rejection is absorbed — every built-in dispatcher reports one as
    /// <see cref="ArgumentException"/> (Cronos' own <see cref="FormatException"/> is accepted for custom
    /// runtimes) and leaves the job untouched. An unparseable MANIFEST cron is an authoring error and still
    /// propagates, like any other invalid flow definition.
    /// </remarks>
    private string RegisterWithFallback(string jobId, Guid flowId, string triggerKey, string manifestCron, string? cronOverride)
    {
        if (string.IsNullOrWhiteSpace(cronOverride))
        {
            _dispatcher.RegisterOrUpdate(jobId, flowId, triggerKey, manifestCron);
            return manifestCron;
        }

        try
        {
            _dispatcher.RegisterOrUpdate(jobId, flowId, triggerKey, cronOverride);
            return cronOverride;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            HostingLog.RecurringJobOverrideRejected(_logger, ex, jobId, Sanitize(cronOverride), Sanitize(manifestCron));
        }

        _dispatcher.RegisterOrUpdate(jobId, flowId, triggerKey, manifestCron);
        return manifestCron;
    }

    private static string Sanitize(string value) => value.Replace('\r', '_').Replace('\n', '_');
}
