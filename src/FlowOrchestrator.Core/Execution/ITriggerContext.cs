using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Combines <see cref="IExecutionContext"/> with the flow definition and the trigger event
/// that started the run. Passed to <see cref="IFlowExecutor.TriggerFlow"/> and
/// <see cref="IFlowGraphPlanner.CreateEntrySteps"/> to bootstrap execution.
/// </summary>
public interface ITriggerContext : IExecutionContext
{
    /// <summary>The flow definition being triggered.</summary>
    IFlowDefinition Flow { get; set; }

    /// <summary>The trigger event that fired (key, type, payload, headers).</summary>
    ITrigger Trigger { get; set; }

    /// <summary>
    /// The Hangfire background job ID of the trigger job, if available.
    /// Stored on the run record for correlation with the Hangfire dashboard.
    /// </summary>
    string? JobId { get; set; }
}
