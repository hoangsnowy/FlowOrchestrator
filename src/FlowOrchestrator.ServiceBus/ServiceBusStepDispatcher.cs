using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.ServiceBus;

/// <summary>
/// Azure Service Bus implementation of <see cref="IStepDispatcher"/> that publishes step
/// envelopes to a shared topic with a per-flow SQL filter.
/// </summary>
/// <remarks>
/// Only <see cref="IFlowDefinition.Id"/> is serialised; the consumer rehydrates the full
/// definition from <c>IFlowRepository</c>. The <c>FlowId</c> application property is the
/// SQL-filter key used by per-flow subscriptions; <c>RunId</c> and <c>StepKey</c> are
/// included for diagnostics. <c>MessageId</c> is shaped <c>{runId}:{stepKey}:{attempt}</c>
/// so duplicate-detection on the topic squashes accidental redelivery — though the
/// engine's <c>TryRecordDispatchAsync</c> ledger is the authoritative idempotency layer.
/// </remarks>
internal sealed class ServiceBusStepDispatcher : IStepDispatcher, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusRuntimeOptions _options;
    private readonly Lazy<ServiceBusSender> _sender;

    /// <summary>Initialises the dispatcher with a shared Service Bus client and options.</summary>
    public ServiceBusStepDispatcher(ServiceBusClient client, ServiceBusRuntimeOptions options)
    {
        _client = client;
        _options = options;
        _sender = new Lazy<ServiceBusSender>(() => _client.CreateSender(_options.StepTopicName));
    }

    /// <inheritdoc/>
    public async ValueTask<string?> EnqueueStepAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance step,
        CancellationToken ct = default)
    {
        var msg = BuildMessage(context, flow, step, scheduledEnqueueAt: null);
        await _sender.Value.SendMessageAsync(msg, ct).ConfigureAwait(false);
        return msg.MessageId;
    }

    /// <inheritdoc/>
    public async ValueTask<string?> ScheduleStepAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance step,
        TimeSpan delay,
        CancellationToken ct = default)
    {
        var when = DateTimeOffset.UtcNow + delay;
        var msg = BuildMessage(context, flow, step, scheduledEnqueueAt: when);
        await _sender.Value.SendMessageAsync(msg, ct).ConfigureAwait(false);
        return msg.MessageId;
    }

    internal static ServiceBusMessage BuildMessage(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance step,
        DateTimeOffset? scheduledEnqueueAt)
    {
        var envelope = StepEnvelope.From(context, flow.Id, step);
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var msg = new ServiceBusMessage(body)
        {
            // MessageId acts as a duplicate-detection key on the topic. Including ScheduledTime
            // ticks ensures Pending-step reschedules produce a fresh id (different ScheduledTime),
            // while genuine duplicate dispatches collide.
            MessageId = $"{context.RunId}:{step.Key}:{step.ScheduledTime.UtcTicks}",
            ContentType = "application/json",
            Subject = step.Key,
        };
        msg.ApplicationProperties["FlowId"] = flow.Id.ToString();
        msg.ApplicationProperties["RunId"] = context.RunId.ToString();
        msg.ApplicationProperties["StepKey"] = step.Key;
        if (scheduledEnqueueAt is { } at)
        {
            msg.ScheduledEnqueueTime = at;
        }
        return msg;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_sender.IsValueCreated)
        {
            await _sender.Value.DisposeAsync().ConfigureAwait(false);
        }
    }
}
