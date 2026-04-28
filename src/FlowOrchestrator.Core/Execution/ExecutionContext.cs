namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Default mutable implementation of <see cref="IExecutionContext"/>.
/// Constructed by <c>HangfireFlowOrchestrator</c> and passed through the step execution pipeline.
/// </summary>
public sealed class ExecutionContext : IExecutionContext
{
    /// <inheritdoc/>
    public Guid RunId { get; set; }

    /// <inheritdoc/>
    public string? PrincipalId { get; set; }

    /// <inheritdoc/>
    public object? TriggerData { get; set; }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string>? TriggerHeaders { get; set; }

    /// <inheritdoc/>
    public string? JobId { get; set; }
}
