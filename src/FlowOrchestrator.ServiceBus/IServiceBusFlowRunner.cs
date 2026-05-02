using Azure.Messaging.ServiceBus;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.ServiceBus;

/// <summary>
/// Internal seam between the Service Bus message processor and the runtime-neutral engine.
/// Mirrors <c>IHangfireStepRunner</c> in the Hangfire adapter — its only job is to extract
/// runtime-specific identifiers from the message and inject them into the execution context
/// before delegating to <see cref="IFlowOrchestrator"/>.
/// </summary>
internal interface IServiceBusFlowRunner
{
    /// <summary>Executes one step described by <paramref name="envelope"/>, sourced from the SB message.</summary>
    /// <param name="envelope">The deserialised step envelope.</param>
    /// <param name="message">The originating Service Bus message; used for diagnostics and JobId correlation.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask RunStepAsync(StepEnvelope envelope, ServiceBusReceivedMessage message, CancellationToken ct);

    /// <summary>Fires a cron-scheduled trigger.</summary>
    /// <param name="flowId">The flow whose schedule fired.</param>
    /// <param name="triggerKey">Manifest trigger key.</param>
    /// <param name="messageId">Service Bus message id used for job correlation.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask TriggerByScheduleAsync(Guid flowId, string triggerKey, string messageId, CancellationToken ct);
}
