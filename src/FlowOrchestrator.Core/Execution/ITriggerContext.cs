using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Combines <see cref="IExecutionContext"/> with the flow definition and the trigger event
/// that started the run. Passed to <see cref="IFlowExecutor.TriggerFlow"/> and
/// <see cref="IFlowGraphPlanner.CreateEntrySteps"/> to bootstrap execution.
/// </summary>
/// <remarks>
/// The runtime-specific job or message ID for the trigger is carried by <see cref="IExecutionContext.JobId"/>,
/// which is set by the runtime adapter (e.g. the Hangfire shim) before invoking the engine.
/// </remarks>
public interface ITriggerContext : IExecutionContext
{
    /// <summary>The flow definition being triggered.</summary>
    IFlowDefinition Flow { get; set; }

    /// <summary>The trigger event that fired (key, type, payload, headers).</summary>
    ITrigger Trigger { get; set; }
}
