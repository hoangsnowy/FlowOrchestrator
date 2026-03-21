namespace FlowOrchestrator.Core.Execution;

public sealed class ExecutionContext : IExecutionContext
{
    public Guid RunId { get; set; }
    public string? PrincipalId { get; set; }
    public object? TriggerData { get; set; }
}
