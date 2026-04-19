using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Untyped step handler contract. Implement this interface (or prefer <see cref="IStepHandler{TInput}"/>)
/// to define the business logic for a step type.
/// Register via <c>AddStepHandler&lt;THandler&gt;(typeName)</c> in DI.
/// </summary>
public interface IStepHandler
{
    /// <summary>
    /// Executes the step and returns an optional result value.
    /// Return a <see cref="IStepResult"/> (e.g. <see cref="StepResult"/> or <see cref="StepResult{T}"/>)
    /// to control status, output, and retry delay; return any other value to implicitly succeed.
    /// </summary>
    /// <param name="context">The ambient execution context for this run (RunId, trigger data, etc.).</param>
    /// <param name="flow">The flow definition currently executing.</param>
    /// <param name="step">The step instance with resolved inputs.</param>
    /// <returns>The step output, or a <see cref="IStepResult"/> to control execution behaviour.</returns>
    ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step);
}

/// <summary>
/// Typed step handler contract. Prefer this over <see cref="IStepHandler"/> when inputs
/// are well-defined — the framework deserialises <see cref="IStepInstance{TInput}.Inputs"/>
/// to <typeparamref name="TInput"/> before invoking the handler.
/// </summary>
/// <typeparam name="TInput">POCO type that maps to the step's <c>inputs</c> dictionary.</typeparam>
public interface IStepHandler<TInput>
{
    /// <summary>
    /// Executes the step with strongly-typed inputs and returns an optional result value.
    /// </summary>
    /// <param name="context">The ambient execution context for this run.</param>
    /// <param name="flow">The flow definition currently executing.</param>
    /// <param name="step">The step instance with inputs already deserialised to <typeparamref name="TInput"/>.</param>
    /// <returns>The step output, or a <see cref="IStepResult"/> to control execution behaviour.</returns>
    ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance<TInput> step);
}
