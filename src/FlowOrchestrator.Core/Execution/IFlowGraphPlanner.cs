using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Analyses a flow's step dependency graph (DAG) to determine execution order,
/// validate structural correctness, and evaluate runtime state.
/// </summary>
internal interface IFlowGraphPlanner
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
    /// Classifies only <paramref name="candidateStepKeys"/> — the steps whose dependencies just changed — as
    /// <c>Ready</c>, <c>Blocked</c> or <c>Waiting</c>, from a status map that holds just those steps and their
    /// dependencies.
    /// </summary>
    /// <param name="flow">The flow definition to evaluate.</param>
    /// <param name="candidateStepKeys">Runtime keys to classify; may contain duplicates.</param>
    /// <param name="statuses">
    /// Status rows of the candidates and of every dependency of every candidate; a key absent from the map has
    /// no row. Rows of unrelated steps may be missing.
    /// </param>
    /// <returns>The candidates grouped by readiness; keys outside the candidate set never appear.</returns>
    /// <remarks>
    /// The engine's per-step-completion evaluation (#189 P0-1): a step's classification depends only on its own
    /// row and its dependencies' rows, so after step <c>X</c> changes only <c>X</c>'s dependents need
    /// classifying. The default implementation runs <see cref="Evaluate"/> on the partial map and keeps the
    /// candidates, which gives the same answer for them; override it with a direct classification of the
    /// candidates, as <see cref="FlowGraphPlanner"/> does, so the cost does not grow with the run.
    /// </remarks>
    FlowGraphEvaluation EvaluateFrontier(
        IFlowDefinition flow,
        IReadOnlyCollection<string> candidateStepKeys,
        IReadOnlyDictionary<string, StepStatus> statuses)
    {
        var full = Evaluate(flow, statuses);
        var wanted = new HashSet<string>(candidateStepKeys, StringComparer.Ordinal);
        return new FlowGraphEvaluation
        {
            ReadyStepKeys = full.ReadyStepKeys.Where(wanted.Contains).ToList(),
            BlockedStepKeys = full.BlockedStepKeys.Where(wanted.Contains).ToList(),
            WaitingStepKeys = full.WaitingStepKeys.Where(wanted.Contains).ToList()
        };
    }

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
