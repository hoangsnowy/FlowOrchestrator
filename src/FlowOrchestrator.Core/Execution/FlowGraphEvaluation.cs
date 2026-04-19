namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Snapshot of a flow run's DAG evaluation at a point in time,
/// produced by <see cref="IFlowGraphPlanner.Evaluate"/>.
/// </summary>
public sealed class FlowGraphEvaluation
{
    /// <summary>
    /// Step keys whose dependencies are all satisfied and that can be enqueued immediately.
    /// </summary>
    public IReadOnlyList<string> ReadyStepKeys { get; init; } = [];

    /// <summary>
    /// Step keys that will never execute because at least one dependency reached a terminal
    /// status that does not satisfy the step's <c>runAfter</c> conditions.
    /// </summary>
    public IReadOnlyList<string> BlockedStepKeys { get; init; } = [];

    /// <summary>
    /// Step keys whose dependencies are still in-progress; evaluation should be re-run
    /// once those dependencies complete.
    /// </summary>
    public IReadOnlyList<string> WaitingStepKeys { get; init; } = [];

    /// <summary>
    /// All step keys known for this run, including runtime-generated loop iteration keys
    /// (e.g. <c>"processItems.0.validate"</c>).
    /// </summary>
    public IReadOnlyList<string> AllKnownStepKeys { get; init; } = [];
}

/// <summary>
/// Result of <see cref="IFlowGraphPlanner.Validate"/>, listing any structural errors
/// found in the flow manifest's step dependency graph.
/// </summary>
public sealed class FlowGraphValidationResult
{
    /// <summary>Human-readable descriptions of validation failures. Empty when the graph is valid.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary><see langword="true"/> when no errors were found.</summary>
    public bool IsValid => Errors.Count == 0;
}
