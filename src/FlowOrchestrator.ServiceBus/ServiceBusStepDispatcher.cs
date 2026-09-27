using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;

namespace FlowOrchestrator.ServiceBus;

/// <summary>
/// Azure Service Bus implementation of <see cref="IStepDispatcher"/> that publishes step
/// envelopes to a shared topic with a per-flow SQL filter.
/// </summary>
/// <remarks>
/// Only <see cref="IFlowDefinition.Id"/> is serialised; the consumer rehydrates the full
/// definition from <c>IFlowRepository</c>. The <c>FlowId</c> application property is the
/// SQL-filter key used by per-flow subscriptions; <c>RunId</c> and <c>StepKey</c> are
/// included for diagnostics. <c>MessageId</c> is shaped <c>{runId}:{stepKey}:{nonce}</c> — unique
/// per dispatch, so topic duplicate-detection only squashes the SDK re-sending one message; the
/// engine's <c>TryRecordDispatchAsync</c> ledger is the authoritative idempotency layer.
/// </remarks>
internal sealed class ServiceBusStepDispatcher : IStepDispatcher, IAsyncDisposable
{
    private const string RuntimeName = "service_bus";

    private readonly ServiceBusClient _client;
    private readonly ServiceBusRuntimeOptions _options;
    private readonly Lazy<ServiceBusSender> _sender;
    private readonly FlowOrchestratorTelemetry? _telemetry;

    /// <summary>Initialises the dispatcher with a shared Service Bus client and options.</summary>
    /// <param name="client">Shared Service Bus client; the dispatcher creates one sender from it.</param>
    /// <param name="options">Runtime options naming the step topic.</param>
    /// <param name="telemetry">Optional — when supplied, each send is timed on <c>flow_step_dispatch_duration_ms</c>.</param>
    /// <param name="observability">Optional — <see cref="FlowObservabilityOptions.EnableOpenTelemetry"/> gates the timing.</param>
    public ServiceBusStepDispatcher(
        ServiceBusClient client,
        ServiceBusRuntimeOptions options,
        FlowOrchestratorTelemetry? telemetry = null,
        FlowObservabilityOptions? observability = null)
    {
        _client = client;
        _options = options;
        _sender = new Lazy<ServiceBusSender>(() => _client.CreateSender(_options.StepTopicName));
        _telemetry = observability?.EnableOpenTelemetry == false ? null : telemetry;
    }

    /// <inheritdoc/>
    public async ValueTask<string?> EnqueueStepAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance step,
        CancellationToken ct = default)
    {
        var msg = BuildMessage(context, flow, step, scheduledEnqueueAt: null);
        await SendAsync(msg, scheduled: false, ct).ConfigureAwait(false);
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
        await SendAsync(msg, scheduled: true, ct).ConfigureAwait(false);
        return msg.MessageId;
    }

    /// <summary>
    /// Sends <paramref name="msg"/> and times the broker round-trip — including the lazy sender's
    /// first-use link attach — on <c>flow_step_dispatch_duration_ms</c>.
    /// </summary>
    /// <remarks>
    /// This is the measurement #192 is missing: the Service Bus runtime showed a 470x p50/p95
    /// trigger-latency spread against the emulator, and request timings cannot tell whether the tail
    /// sits on this send or elsewhere.
    /// </remarks>
    private async Task SendAsync(ServiceBusMessage msg, bool scheduled, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        var ok = false;
        try
        {
            await _sender.Value.SendMessageAsync(msg, ct).ConfigureAwait(false);
            ok = true;
        }
        finally
        {
            _telemetry?.RecordDispatch(RuntimeName, scheduled, ok, start);
        }
    }

    /// <summary>Service Bus rejects a <c>MessageId</c> longer than this.</summary>
    internal const int MaxMessageIdLength = 128;

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
            MessageId = BuildMessageId(context.RunId, step.Key),
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

    /// <summary>
    /// Builds a <c>MessageId</c> that is unique per dispatch by construction.
    /// </summary>
    /// <param name="runId">Run the step belongs to; kept in the id for diagnostics.</param>
    /// <param name="stepKey">Step being dispatched; kept in the id for diagnostics when it fits.</param>
    /// <returns>
    /// <c>{runId}:{stepKey}:{nonce}</c>, or <c>{runId}:{nonce}</c> when the step key would push the id
    /// past <see cref="MaxMessageIdLength"/> (the key is still carried in <c>Subject</c> and the
    /// <c>StepKey</c> application property).
    /// </returns>
    /// <remarks>
    /// The step topic has duplicate detection on with a 10-minute history window, so the broker
    /// <b>silently drops</b> any second message carrying an id it has already seen — no error, no
    /// dead-letter. Up to v1.32 the id was <c>{runId}:{stepKey}:{ScheduledTime ticks}</c>, which made
    /// the correctness of every Pending reschedule depend on no two dispatches of one step ever sharing
    /// a tick inside that window (#186). The engine's dispatch ledger and execution claim are the
    /// idempotency layers; broker dedup only has to squash the SDK's own retry of a send whose first
    /// attempt actually landed, and that still works because a retry reuses this same message
    /// instance and therefore this same id.
    /// </remarks>
    internal static string BuildMessageId(Guid runId, string stepKey)
    {
        var nonce = Guid.NewGuid();
        var id = $"{runId}:{stepKey}:{nonce:N}";
        return id.Length <= MaxMessageIdLength ? id : $"{runId}:{nonce:N}";
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
