using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.ServiceBus;

/// <summary>
/// Background service that drains the cron queue, fires the corresponding schedule trigger
/// on the engine, and self-perpetuates the next firing as a scheduled message.
/// </summary>
/// <remarks>
/// <para>
/// MaxConcurrentCalls is 1 so that the consumer processes one cron tick at a time —
/// cron is low-throughput and ordering is more useful than parallelism.
/// </para>
/// <para>
/// A recurring tick is fired only when it is still wanted. It is dropped (completed without a run and
/// without scheduling a successor, which ends its chain) when:
/// </para>
/// <list type="bullet">
///   <item>the persisted state says the schedule is inactive — the flow is disabled or no longer declares the
///         cron trigger, or the job is paused on the dashboard (with <see cref="FlowSchedulerOptions.PersistOverrides"/>,
///         so a pause made on another replica counts);</item>
///   <item>the job is not registered in this process and no persisted state says otherwise;</item>
///   <item>the tick belongs to a chain the job has replaced (a cron change, a restart, a resume) — known
///         locally from the chain epoch, or on any replica from a persisted cron override that differs from
///         the tick's cron;</item>
///   <item>the same tick already fired here (a redelivery, or a second copy of one tick).</item>
/// </list>
/// <para>
/// Processing starts only once the host has started (<see cref="IHostApplicationLifetime.ApplicationStarted"/>),
/// i.e. after the startup sync registered every job, and after the hub's startup seeds are settled.
/// </para>
/// </remarks>
internal sealed partial class ServiceBusCronProcessorHostedService : BackgroundService
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusRuntimeOptions _options;
    private readonly ServiceBusRecurringTriggerHub _hub;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ServiceBusCronProcessorHostedService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IHostApplicationLifetime? _lifetime;

    private ServiceBusProcessor? _processor;

    /// <summary>Initialises the service.</summary>
    /// <param name="client">Service Bus client the cron-queue processor is created from.</param>
    /// <param name="options">Runtime options (cron queue name).</param>
    /// <param name="hub">The recurring-trigger hub that owns the job registrations and chains.</param>
    /// <param name="scopeFactory">Creates the per-tick scope the engine and stores are resolved from.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">Clock; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="lifetime">
    /// Host lifetime; when supplied, processing waits for <see cref="IHostApplicationLifetime.ApplicationStarted"/>
    /// so no tick is judged before the startup sync has registered the jobs.
    /// </param>
    public ServiceBusCronProcessorHostedService(
        ServiceBusClient client,
        ServiceBusRuntimeOptions options,
        ServiceBusRecurringTriggerHub hub,
        IServiceScopeFactory scopeFactory,
        ILogger<ServiceBusCronProcessorHostedService> logger,
        TimeProvider? timeProvider = null,
        IHostApplicationLifetime? lifetime = null)
    {
        _client = client;
        _options = options;
        _hub = hub;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifetime = lifetime;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // FlowSyncHostedService is registered after the runtime's hosted services, so it registers the
            // jobs AFTER this service has started. A tick judged before that would look unregistered and be
            // dropped, ending its chain.
            await WaitForHostStartedAsync(stoppingToken).ConfigureAwait(false);
            await _hub.CompleteStartupAsync().ConfigureAwait(false);

            _processor = _client.CreateProcessor(_options.CronQueueName, new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls = 1,
                AutoCompleteMessages = false,
                ReceiveMode = ServiceBusReceiveMode.PeekLock,
            });
            _processor.ProcessMessageAsync += OnMessageAsync;
            _processor.ProcessErrorAsync += OnErrorAsync;

            await _processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* shutting down */ }
        finally
        {
            if (_processor is not null)
            {
                // Dispose path: any exception (incl. OCE) must not break shutdown.
                try { await _processor.StopProcessingAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception ex) when (ex is not null) { /* swallow */ }
                await _processor.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task WaitForHostStartedAsync(CancellationToken stoppingToken)
    {
        if (_lifetime is null)
        {
            return;
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var onStarted = _lifetime.ApplicationStarted.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), started);
        using var onStopping = stoppingToken.Register(static s => ((TaskCompletionSource)s!).TrySetCanceled(), started);
        await started.Task.ConfigureAwait(false);
    }

    private async Task OnMessageAsync(ProcessMessageEventArgs args)
    {
        CronEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<CronEnvelope>(args.Message.Body.ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDeserialisationFailed(_logger, ex, args.Message.MessageId);
            await args.DeadLetterMessageAsync(args.Message, "deserialisation-failed", ex.Message, args.CancellationToken).ConfigureAwait(false);
            return;
        }

        if (envelope is null)
        {
            await args.DeadLetterMessageAsync(args.Message, "null-envelope", null, args.CancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();

            var recurring = !string.IsNullOrEmpty(envelope.Cron);
            if (recurring)
            {
                if (await ShouldDropAsync(scope.ServiceProvider, envelope).ConfigureAwait(false))
                {
                    // Completing without a successor ends this chain; the job's current chain (if any) is a
                    // different message stream.
                    await args.CompleteMessageAsync(args.Message, args.CancellationToken).ConfigureAwait(false);
                    return;
                }

                // Self-perpetuate first: schedule the next firing BEFORE completing the current
                // message. If ScheduleNextAsync throws (transient SB error), abandon-redeliver lets
                // us retry on the same message — this is what protects the cron loop from going dark
                // forever after a single broker blip. If we crash AFTER scheduling but BEFORE
                // CompleteMessageAsync, the message is redelivered and duplicate detection swallows
                // the next-fire enqueue, so we don't get two fires for the same tick.
                //
                // v1.22.0 cron-drift fix: compute the next fire from envelope.ScheduledFor (the
                // tick we just consumed), not from the consumer's wall-clock. Otherwise a backlogged
                // consumer skips ticks. Clamp to "now or later" so a cron whose schedule already
                // passed (e.g. processed late) still fires the upcoming tick rather than the past one.
                var fromInstant = envelope.ScheduledFor == default
                    ? _timeProvider.GetUtcNow()
                    : envelope.ScheduledFor;
                var nextFire = ServiceBusRecurringTriggerHub.ComputeNext(envelope.Cron, fromInstant);
                if (nextFire <= _timeProvider.GetUtcNow())
                {
                    nextFire = ServiceBusRecurringTriggerHub.ComputeNext(envelope.Cron, _timeProvider.GetUtcNow());
                }
                await _hub.ScheduleNextAsync(
                        envelope.JobId, envelope.FlowId, envelope.TriggerKey, envelope.Cron, nextFire, envelope.ChainEpoch, args.CancellationToken)
                    .ConfigureAwait(false);
            }

            var runner = scope.ServiceProvider.GetRequiredService<IServiceBusFlowRunner>();
            await runner.TriggerByScheduleAsync(envelope.FlowId, envelope.TriggerKey, args.Message.MessageId, args.CancellationToken)
                        .ConfigureAwait(false);
            _hub.NoteFireResult(envelope.JobId, "Succeeded", _timeProvider.GetUtcNow(), recurring ? envelope.ScheduledFor : null);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _hub.NoteFireResult(envelope.JobId, "Failed", _timeProvider.GetUtcNow());
            LogFireFailed(_logger, ex, Sanitize(envelope.JobId), envelope.FlowId, Sanitize(envelope.TriggerKey));
            // Abandon — let SB redeliver up to MaxDeliveryCount, after which it dead-letters.
            await args.AbandonMessageAsync(args.Message, propertiesToModify: null, args.CancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Decides whether a recurring tick must be dropped instead of fired; see the class remarks.
    /// </summary>
    /// <param name="services">The tick's scope.</param>
    /// <param name="envelope">The recurring tick.</param>
    /// <returns><see langword="true"/> to complete the tick without a run and without a successor.</returns>
    private async Task<bool> ShouldDropAsync(IServiceProvider services, CronEnvelope envelope)
    {
        var authority = await ResolveAuthorityAsync(services, envelope).ConfigureAwait(false);
        if (authority == ScheduleAuthority.Inactive)
        {
            // Align this process with the shared state (the pause or disable may have been made on another
            // replica). Dropping the tick ends the job's chain, so a later resume here must seed a new one —
            // which RegisterOrUpdate only does for a job it does not already hold.
            _hub.Remove(envelope.JobId);
            LogTickDropped(_logger, Sanitize(envelope.JobId), envelope.ScheduledFor, "the schedule is paused, or its flow is disabled or no longer declares the trigger");
            return true;
        }

        if (authority == ScheduleAuthority.Superseded)
        {
            // The persisted schedule runs on another cron: this tick belongs to a replaced chain. Every replica
            // sharing the store reaches the same answer, so a replica that has not yet seen the new chain does
            // not fire (and perpetuate) the old one. The job itself stays registered — it lives on the new chain.
            LogTickDropped(_logger, Sanitize(envelope.JobId), envelope.ScheduledFor, "its cron differs from the schedule's persisted cron override (the chain was replaced)");
            return true;
        }

        var verdict = _hub.ObserveTick(envelope, registerIfMissing: authority == ScheduleAuthority.Active);
        switch (verdict)
        {
            case ServiceBusRecurringTriggerHub.TickVerdict.Current:
                return false;
            case ServiceBusRecurringTriggerHub.TickVerdict.NotRegistered:
                LogTickDropped(_logger, Sanitize(envelope.JobId), envelope.ScheduledFor, "the job is not registered (paused or removed)");
                return true;
            case ServiceBusRecurringTriggerHub.TickVerdict.Superseded:
                LogTickDropped(_logger, Sanitize(envelope.JobId), envelope.ScheduledFor, "it belongs to a chain the job has replaced (cron change, restart or resume)");
                return true;
            default:
                LogTickDropped(_logger, Sanitize(envelope.JobId), envelope.ScheduledFor, "this tick already fired");
                return true;
        }
    }

    /// <summary>
    /// Reads the shared, persisted view of the schedule: the flow registry, the flow's enabled flag and — with
    /// <see cref="FlowSchedulerOptions.PersistOverrides"/> — the dashboard pause state.
    /// </summary>
    /// <param name="services">The tick's scope.</param>
    /// <param name="envelope">The recurring tick.</param>
    /// <returns>
    /// <see cref="ScheduleAuthority.Inactive"/> when the tick must not fire whatever this process believes;
    /// <see cref="ScheduleAuthority.Active"/> when the shared state says the job is live;
    /// <see cref="ScheduleAuthority.Unknown"/> when only this process's registration can decide (no shared
    /// pause state, services missing, or the store unreachable).
    /// </returns>
    /// <remarks>Two store reads per recurring tick (flow record, schedule state) — cron-rate, not step-rate.</remarks>
    private async Task<ScheduleAuthority> ResolveAuthorityAsync(IServiceProvider services, CronEnvelope envelope)
    {
        var flows = services.GetService<FlowRegistry>();
        var flowStore = services.GetService<IFlowStore>();
        if (flows is null || flowStore is null)
        {
            return ScheduleAuthority.Unknown;
        }

        if (!flows.TryGetFlow(envelope.FlowId, out var flow)
            || !flow.Manifest.Triggers.TryGetValue(envelope.TriggerKey, out var trigger)
            || trigger.Type != TriggerType.Cron
            || !trigger.TryGetCronExpression(out _))
        {
            return ScheduleAuthority.Inactive;
        }

        try
        {
            var record = await flowStore.GetByIdAsync(envelope.FlowId).ConfigureAwait(false);
            if (record is { IsEnabled: false })
            {
                return ScheduleAuthority.Inactive;
            }

            var schedulerOptions = services.GetService<FlowSchedulerOptions>();
            var stateStore = services.GetService<IFlowScheduleStateStore>();
            if (schedulerOptions is not { PersistOverrides: true } || stateStore is null)
            {
                // The pause state is process-local: only this process's registration knows it.
                return ScheduleAuthority.Unknown;
            }

            var state = await stateStore.GetAsync(envelope.JobId).ConfigureAwait(false);
            if (state?.IsPaused == true)
            {
                return ScheduleAuthority.Inactive;
            }

            // A valid persisted override is the job's cron on every replica. An unparseable one is ignored, as
            // the startup sync ignores it (the job then runs on the manifest cron).
            if (!string.IsNullOrWhiteSpace(state?.CronOverride)
                && !string.Equals(state.CronOverride, envelope.Cron, StringComparison.Ordinal)
                && IsParseable(state.CronOverride))
            {
                return ScheduleAuthority.Superseded;
            }

            return ScheduleAuthority.Active;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAuthorityUnavailable(_logger, ex, Sanitize(envelope.JobId));
            return ScheduleAuthority.Unknown;
        }
    }

    private bool IsParseable(string cron)
    {
        try
        {
            ServiceBusRecurringTriggerHub.ComputeNext(cron, _timeProvider.GetUtcNow());
            return true;
        }
        catch (Cronos.CronFormatException)
        {
            return false;
        }
    }

    private Task OnErrorAsync(ProcessErrorEventArgs args)
    {
        LogProcessorError(_logger, args.Exception, args.ErrorSource.ToString(), args.EntityPath);
        return Task.CompletedTask;
    }

    private static string Sanitize(string value) => value.Replace('\r', '_').Replace('\n', '_');

    [LoggerMessage(EventId = 3150, Level = LogLevel.Error, Message = "Failed to deserialise cron message {MessageId}; dead-lettering.")]
    private static partial void LogDeserialisationFailed(ILogger logger, Exception ex, string messageId);

    [LoggerMessage(EventId = 3151, Level = LogLevel.Error, Message = "Cron firing failed for {JobId} (Flow={FlowId}, TriggerKey={TriggerKey}).")]
    private static partial void LogFireFailed(ILogger logger, Exception ex, string jobId, Guid flowId, string triggerKey);

    [LoggerMessage(EventId = 3152, Level = LogLevel.Information, Message = "Dropped cron tick for {JobId} scheduled for {ScheduledFor:O}: {Reason}.")]
    private static partial void LogTickDropped(ILogger logger, string jobId, DateTimeOffset scheduledFor, string reason);

    [LoggerMessage(EventId = 3153, Level = LogLevel.Warning, Message = "Could not read the persisted schedule state for {JobId}; deciding the cron tick on this process's registration only.")]
    private static partial void LogAuthorityUnavailable(ILogger logger, Exception ex, string jobId);

    [LoggerMessage(EventId = 3154, Level = LogLevel.Error, Message = "Service Bus cron processor error (Source={Source}, Entity={Entity}).")]
    private static partial void LogProcessorError(ILogger logger, Exception ex, string source, string entity);

    /// <summary>What the shared, persisted state says about a recurring job.</summary>
    private enum ScheduleAuthority
    {
        /// <summary>No shared view is available; this process's registration decides.</summary>
        Unknown,

        /// <summary>The shared state says the job is live.</summary>
        Active,

        /// <summary>The shared state says the job must not fire.</summary>
        Inactive,

        /// <summary>The shared state says the job runs on another cron: the tick belongs to a replaced chain.</summary>
        Superseded,
    }
}
