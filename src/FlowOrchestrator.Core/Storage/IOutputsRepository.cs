using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Stores and retrieves per-run step outputs, trigger data, and events.
/// Outputs are keyed by RunId + step key and resolved at expression-evaluation time
/// to satisfy <c>@outputs('stepKey')</c> references in downstream steps.
/// </summary>
public interface IOutputsRepository
{
    /// <summary>Persists the output of a completed step for later retrieval by downstream steps.</summary>
    ValueTask SaveStepOutputAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, IStepResult result);

    /// <summary>Persists the trigger payload for the run so steps can access it via <c>@triggerBody()</c>.</summary>
    ValueTask SaveTriggerDataAsync(ITriggerContext ctx, IFlowDefinition flow, ITrigger trigger);

    /// <summary>Retrieves the raw trigger payload for the given run, or <see langword="null"/> if not stored.</summary>
    ValueTask<object?> GetTriggerDataAsync(Guid runId);

    /// <summary>Persists the trigger HTTP headers for the run so steps can access them via <c>@triggerHeaders()</c>.</summary>
    ValueTask SaveTriggerHeadersAsync(ITriggerContext ctx, IFlowDefinition flow, ITrigger trigger);

    /// <summary>Retrieves the trigger headers for the given run, or <see langword="null"/> if not stored.</summary>
    ValueTask<IReadOnlyDictionary<string, string>?> GetTriggerHeadersAsync(Guid runId);

    /// <summary>Persists the resolved inputs for a step (used for audit and retry replay).</summary>
    ValueTask SaveStepInputAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step);

    /// <summary>
    /// Retrieves the output of a step for the given run and step key,
    /// or <see langword="null"/> if the step has not yet completed.
    /// </summary>
    ValueTask<object?> GetStepOutputAsync(Guid runId, string stepKey);

    /// <summary>
    /// Signals that the scoped step (loop iteration) identified by <paramref name="step"/> is complete.
    /// Implementations may use this to aggregate loop outputs or release scope-level resources.
    /// </summary>
    ValueTask EndScopeAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step);

    /// <summary>Appends a <see cref="FlowEvent"/> to the event log for the run.</summary>
    ValueTask RecordEventAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, FlowEvent evt);
}
