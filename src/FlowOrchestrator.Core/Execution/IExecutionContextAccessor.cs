namespace FlowOrchestrator.Core.Execution;

public interface IExecutionContextAccessor
{
    IExecutionContext? CurrentContext { get; set; }
}
