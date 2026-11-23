using Azure.Messaging.ServiceBus;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.ServiceBus;

/// <summary>
/// Thin Service Bus adapter that bridges <see cref="ServiceBusReceivedMessage"/> to the
/// runtime-neutral <see cref="IFlowOrchestrator"/> engine. Mirrors <c>HangfireFlowOrchestrator</c>.
/// </summary>
/// <remarks>
/// Step messages carry only a <see cref="Guid"/> flow id. The full <c>IFlowDefinition</c> is
/// resolved from <see cref="FlowRegistry"/> on the worker — the manifest never leaves
/// the process boundary, which avoids polymorphism issues in JSON serialisation and keeps
/// flow code as the single source of truth.
/// </remarks>
internal sealed class ServiceBusFlowOrchestrator : IServiceBusFlowRunner
{
    private readonly IFlowOrchestrator _engine;
    private readonly FlowRegistry _flows;

    /// <summary>Initialises the adapter with the engine and flow registry.</summary>
    public ServiceBusFlowOrchestrator(IFlowOrchestrator engine, FlowRegistry flows)
    {
        _engine = engine;
        _flows = flows;
    }

    /// <inheritdoc/>
    public async ValueTask RunStepAsync(StepEnvelope envelope, ServiceBusReceivedMessage message, CancellationToken ct)
    {
        var flow = _flows.Find(envelope.FlowId)
            ?? throw new InvalidOperationException(
                $"Flow {envelope.FlowId} is not registered. Cannot dispatch step '{envelope.StepKey}'.");
        var ctx = envelope.ToExecutionContext();
        ctx.JobId = message?.MessageId;
        var step = envelope.ToStepInstance();
        await _engine.RunStepAsync(ctx, flow, step, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask TriggerByScheduleAsync(Guid flowId, string triggerKey, string messageId, CancellationToken ct)
    {
        await _engine.TriggerByScheduleAsync(flowId, triggerKey, messageId, ct).ConfigureAwait(false);
    }
}
