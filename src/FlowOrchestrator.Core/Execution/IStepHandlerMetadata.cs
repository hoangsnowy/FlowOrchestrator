using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Registered singleton that associates a step type name with its handler implementation,
/// and provides the bridge to resolve and invoke the handler from DI at execution time.
/// </summary>
public interface IStepHandlerMetadata
{
    /// <summary>
    /// The logical type name that matches the <c>"type"</c> field in the flow manifest
    /// (e.g. <c>"HttpCall"</c>, <c>"SendEmail"</c>).
    /// </summary>
    string Type { get; }

    /// <summary>
    /// Resolves the handler from <paramref name="sp"/>, deserialises step inputs, invokes the handler,
    /// and returns a normalised <see cref="IStepResult"/>.
    /// </summary>
    /// <param name="sp">The DI service provider scoped to the current Hangfire job.</param>
    /// <param name="ctx">The ambient execution context.</param>
    /// <param name="flow">The flow definition currently executing.</param>
    /// <param name="step">The step instance with resolved inputs.</param>
    ValueTask<IStepResult> ExecuteAsync(IServiceProvider sp, IExecutionContext ctx, IFlowDefinition flow, IStepInstance step);
}
