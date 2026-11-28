using FlowOrchestrator.Core.Execution;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Hangfire implementation of <see cref="IRecurringTriggerDispatcher"/>.
/// Delegates cron-schedule management to <see cref="IRecurringJobManager"/>
/// and one-off enqueues to <see cref="IBackgroundJobClient"/>.
/// </summary>
/// <remarks>
/// Effective-cron resolution (pause state, persisted overrides) lives in Core's
/// <c>RecurringTriggerSync</c>, which calls <see cref="RegisterOrUpdate"/> / <see cref="Remove"/>.
/// </remarks>
internal sealed partial class HangfireRecurringTriggerDispatcher : IRecurringTriggerDispatcher
{
    private readonly IRecurringJobManager _manager;
    private readonly IBackgroundJobClient _client;
    private readonly ILogger<HangfireRecurringTriggerDispatcher> _logger;

    /// <summary>Initialises the dispatcher with required Hangfire services.</summary>
    /// <param name="manager">Hangfire recurring-job manager.</param>
    /// <param name="client">Hangfire job client used for one-off trigger enqueues.</param>
    /// <param name="logger">Optional logger; defaults to a no-op logger.</param>
    public HangfireRecurringTriggerDispatcher(
        IRecurringJobManager manager,
        IBackgroundJobClient client,
        ILogger<HangfireRecurringTriggerDispatcher>? logger = null)
    {
        _manager = manager;
        _client = client;
        _logger = logger ?? NullLogger<HangfireRecurringTriggerDispatcher>.Instance;
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
        LogRegistered(_logger, Sanitize(jobId), Sanitize(cronExpression));
    }

    /// <inheritdoc/>
    public void Remove(string jobId)
    {
        _manager.RemoveIfExists(jobId);
        LogRemoved(_logger, Sanitize(jobId));
    }

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

    // Job ids and cron expressions can arrive from dashboard URLs and bodies; strip line breaks so
    // they cannot forge log entries.
    private static string Sanitize(string value) => value.Replace('\r', '_').Replace('\n', '_');

    [LoggerMessage(EventId = 3100, Level = LogLevel.Information, Message = "Registered recurring job {JobId} with cron '{Cron}'.")]
    private static partial void LogRegistered(ILogger logger, string jobId, string cron);

    [LoggerMessage(EventId = 3101, Level = LogLevel.Information, Message = "Removed recurring job {JobId}.")]
    private static partial void LogRemoved(ILogger logger, string jobId);
}
