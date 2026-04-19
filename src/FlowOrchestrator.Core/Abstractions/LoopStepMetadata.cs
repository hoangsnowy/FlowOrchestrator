namespace FlowOrchestrator.Core.Abstractions;

/// <summary>
/// Step metadata for a <c>foreach</c> loop that iterates over a collection
/// and executes nested <see cref="IScopedStep.Steps"/> for each item.
/// </summary>
public sealed class LoopStepMetadata : StepMetadata, IScopedStep
{
    /// <summary>
    /// The collection to iterate over. Can be a static array or a runtime expression
    /// (e.g. <c>"@triggerBody()?.items"</c>) resolved against the execution context.
    /// </summary>
    public object? ForEach { get; set; }

    /// <summary>
    /// Maximum number of iterations that may run in parallel.
    /// Defaults to <c>1</c> (sequential). Set higher for parallel fan-out.
    /// </summary>
    public int ConcurrencyLimit { get; set; } = 1;

    /// <summary>Steps to execute for each item in <see cref="ForEach"/>.</summary>
    public StepCollection Steps { get; set; } = new();
}
