namespace FlowOrchestrator.Core.Execution;

public sealed class ExecutionContextAccessor : IExecutionContextAccessor
{
    public IExecutionContext? CurrentContext { get; set; }
}
