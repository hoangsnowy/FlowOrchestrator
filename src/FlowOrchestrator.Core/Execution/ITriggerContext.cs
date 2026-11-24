using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Combines <see cref="IExecutionContext"/> with the flow definition and the trigger event
/// that started the run. Passed to <see cref="IFlowGraphPlanner.CreateEntrySteps"/> to bootstrap
/// execution.
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

    /// <summary>
    /// Optional lineage pointer — when set, persisted on the new run as <c>SourceRunId</c>
    /// so the dashboard can render a link back to the original. Used by "Re-run all".
    /// </summary>
    Guid? SourceRunId { get => null; set { } }

    /// <summary>
    /// Set by the engine once <see cref="IFlowOrchestrator.TriggerAsync"/> has dispatched or skipped at least one
    /// entry step — from then on the run is alive (recovery re-dispatches it) even if the trigger call later throws.
    /// </summary>
    /// <remarks>
    /// A caller that sees <c>TriggerAsync</c> throw while this is still <see langword="false"/> knows no work was
    /// started for the delivery, so letting the sender retry it cannot duplicate a run. The webhook endpoint uses
    /// it to decide whether to release the delivery's replay-protection key. The default implementation does not
    /// store the value (it always reads <see langword="false"/>); <see cref="TriggerContext"/> does.
    /// </remarks>
    bool EntryStepsStarted { get => false; set { } }
}
