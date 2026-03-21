namespace FlowOrchestrator.Core.Execution;

public interface IExecutionContext
{
    Guid RunId { get; set; }
    string? PrincipalId { get; set; }
    object? TriggerData { get; set; }
}
