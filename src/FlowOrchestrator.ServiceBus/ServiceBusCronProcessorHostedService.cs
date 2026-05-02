using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.ServiceBus;

/// <summary>
/// Background service that drains the cron queue, fires the corresponding schedule trigger
/// on the engine, and self-perpetuates the next firing as a scheduled message.
/// </summary>
/// <remarks>
/// MaxConcurrentCalls is 1 so that the consumer processes one cron tick at a time —
/// cron is low-throughput and ordering is more useful than parallelism.
/// </remarks>
internal sealed class ServiceBusCronProcessorHostedService : BackgroundService
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusRuntimeOptions _options;
    private readonly ServiceBusRecurringTriggerHub _hub;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ServiceBusCronProcessorHostedService> _logger;
    private readonly TimeProvider _timeProvider;

    private ServiceBusProcessor? _processor;

    /// <summary>Initialises the service.</summary>
    public ServiceBusCronProcessorHostedService(
        ServiceBusClient client,
        ServiceBusRuntimeOptions options,
        ServiceBusRecurringTriggerHub hub,
        IServiceScopeFactory scopeFactory,
        ILogger<ServiceBusCronProcessorHostedService> logger,
        TimeProvider? timeProvider = null)
    {
        _client = client;
        _options = options;
        _hub = hub;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processor = _client.CreateProcessor(_options.CronQueueName, new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = 1,
            AutoCompleteMessages = false,
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
        });
        _processor.ProcessMessageAsync += OnMessageAsync;
        _processor.ProcessErrorAsync += OnErrorAsync;

        try
        {
            await _processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            try { await _processor.StopProcessingAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* swallow */ }
            await _processor.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task OnMessageAsync(ProcessMessageEventArgs args)
    {
        CronEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<CronEnvelope>(args.Message.Body.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialise cron message {MessageId}; dead-lettering.", args.Message.MessageId);
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
            // Self-perpetuate first: schedule the next firing BEFORE completing the current
            // message. If ScheduleNextAsync throws (transient SB error), abandon-redeliver lets
            // us retry on the same message — this is what protects the cron loop from going dark
            // forever after a single broker blip. If we crash AFTER scheduling but BEFORE
            // CompleteMessageAsync, the message is redelivered and duplicate detection swallows
            // the next-fire enqueue, so we don't get two fires for the same tick.
            //
            // Note on cron drift under backlog: ComputeNext uses the consumer's wall-clock
            // (drain time) rather than envelope.ScheduledFor. A backlogged consumer skips
            // ticks instead of bursting catch-up — by design.
            if (!string.IsNullOrEmpty(envelope.Cron))
            {
                var nextFire = ServiceBusRecurringTriggerHub.ComputeNext(envelope.Cron, _timeProvider.GetUtcNow());
                await _hub.ScheduleNextAsync(envelope.JobId, envelope.FlowId, envelope.TriggerKey, envelope.Cron, nextFire, args.CancellationToken)
                           .ConfigureAwait(false);
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<IServiceBusFlowRunner>();
            await runner.TriggerByScheduleAsync(envelope.FlowId, envelope.TriggerKey, args.Message.MessageId, args.CancellationToken)
                        .ConfigureAwait(false);
            _hub.NoteFireResult(envelope.JobId, "Succeeded", _timeProvider.GetUtcNow());
            await args.CompleteMessageAsync(args.Message, args.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _hub.NoteFireResult(envelope.JobId, "Failed", _timeProvider.GetUtcNow());
            _logger.LogError(ex,
                "Cron firing failed for {JobId} (Flow={FlowId}, TriggerKey={TriggerKey}).",
                envelope.JobId, envelope.FlowId, envelope.TriggerKey);
            // Abandon — let SB redeliver up to MaxDeliveryCount, after which it dead-letters.
            await args.AbandonMessageAsync(args.Message, propertiesToModify: null, args.CancellationToken).ConfigureAwait(false);
        }
    }

    private Task OnErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "Service Bus cron processor error (Source={Source}, Entity={Entity}).",
            args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }
}
