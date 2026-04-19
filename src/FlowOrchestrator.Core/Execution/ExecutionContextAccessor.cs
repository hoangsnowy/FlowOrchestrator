namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Default scoped implementation of <see cref="IExecutionContextAccessor"/>.
/// One instance per DI scope (one per Hangfire job), populated immediately before step handler invocation.
/// </summary>
public sealed class ExecutionContextAccessor : IExecutionContextAccessor
{
    /// <inheritdoc/>
    public IExecutionContext? CurrentContext { get; set; }
}
