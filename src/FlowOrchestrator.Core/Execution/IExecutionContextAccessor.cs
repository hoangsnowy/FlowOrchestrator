namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Provides scoped access to the <see cref="IExecutionContext"/> for the currently executing step.
/// Registered as a scoped service; each Hangfire job scope gets its own instance populated by
/// <c>DefaultStepExecutor</c> before the step handler is invoked.
/// </summary>
public interface IExecutionContextAccessor
{
    /// <summary>
    /// The context for the currently executing step, or <see langword="null"/> when accessed
    /// outside of an active step execution scope.
    /// </summary>
    IExecutionContext? CurrentContext { get; set; }
}
