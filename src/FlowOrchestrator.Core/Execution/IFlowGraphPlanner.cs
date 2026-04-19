using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Analyses a flow's step dependency graph (DAG) to determine execution order,
/// validate structural correctness, and evaluate runtime state.
/// </summary>
public interface IFlowGraphPlanner
{
    /// <summary>
    /// Returns all entry steps (steps with no <c>runAfter</c> dependencies) as
    /// ready-to-enqueue <see cref="IStepInstance"/> objects.
    /// </summary>
    /// <param name="context">The trigger context supplying the flow definition and run metadata.</param>
    IReadOnlyList<IStepInstance> CreateEntrySteps(ITriggerContext context);

    /// <summary>
    /// Evaluates the current runtime state of a flow run and categorises every known step as
    /// <c>Ready</c>, <c>Blocked</c>, or <c>Waiting</c>.
    /// </summary>
    /// <param name="flow">The flow definition to evaluate.</param>
    /// <param name="statuses">
    /// A snapshot of step statuses already recorded for the run.
    /// Steps absent from this map are considered not-yet-started.
    /// </param>
    /// <returns>
    /// A <see cref="FlowGraphEvaluation"/> grouping step keys by their current readiness.
    /// </returns>
    FlowGraphEvaluation Evaluate(IFlowDefinition flow, IReadOnlyDictionary<string, StepStatus> statuses);

    /// <summary>
    /// Validates the structural correctness of the flow manifest's step graph at startup.
    /// Checks for missing entry steps, unresolvable dependencies, and dependency cycles.
    /// </summary>
    /// <param name="flow">The flow definition to validate.</param>
    /// <returns>
    /// A <see cref="FlowGraphValidationResult"/> with a list of error messages.
    /// <see cref="FlowGraphValidationResult.IsValid"/> is <see langword="true"/> when the list is empty.
    /// </returns>
    FlowGraphValidationResult Validate(IFlowDefinition flow);
}
