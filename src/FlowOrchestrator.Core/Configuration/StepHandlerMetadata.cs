using System.Reflection;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration.Internal;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Core.Configuration;

/// <summary>
/// DI bridge that resolves a registered <typeparamref name="THandler"/> from the service provider,
/// deserializes step inputs to the handler's strongly-typed input model, and invokes
/// <see cref="IStepHandler"/> or <see cref="IStepHandler{TInput}"/> accordingly.
/// </summary>
/// <typeparam name="THandler">The handler class registered via <c>AddStepHandler&lt;T&gt;()</c>.</typeparam>
internal sealed class StepHandlerMetadata<THandler> : IStepHandlerMetadata
    where THandler : class
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly MethodInfo _invokeGenericHandlerMethod = typeof(StepHandlerMetadata<THandler>)
        .GetMethod(nameof(InvokeGenericHandlerAsync), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"Could not locate method '{nameof(InvokeGenericHandlerAsync)}'.");

    /// <summary>Initialises the metadata with the handler's registered type name.</summary>
    /// <param name="type">The step type name used to match steps in flow manifests.</param>
    public StepHandlerMetadata(string type)
    {
        Type = type;
    }

    /// <summary>Step type name as registered via <c>AddStepHandler&lt;T&gt;("TypeName")</c>.</summary>
    public string Type { get; }

    /// <summary>
    /// Resolves <typeparamref name="THandler"/> from <paramref name="sp"/>, deserializes inputs,
    /// and executes the handler, returning a normalised <see cref="IStepResult"/>.
    /// </summary>
    /// <param name="sp">The request-scoped service provider.</param>
    /// <param name="ctx">The current execution context carrying RunId and trigger data.</param>
    /// <param name="flow">The flow definition containing the step declaration.</param>
    /// <param name="step">The step instance with resolved inputs.</param>
    public async ValueTask<IStepResult> ExecuteAsync(IServiceProvider sp, IExecutionContext ctx, IFlowDefinition flow, IStepInstance step)
    {
        var handler = sp.GetRequiredService<THandler>();
        object? result;

        switch (handler)
        {
            case IStepHandler typedHandler:
                result = await typedHandler.ExecuteAsync(ctx, flow, step).ConfigureAwait(false);
                break;
            default:
                result = await ExecuteGenericHandlerAsync(handler, ctx, flow, step).ConfigureAwait(false);
                break;
        }

        if (result is IStepResult stepResult)
        {
            if (string.IsNullOrWhiteSpace(stepResult.Key))
            {
                stepResult.Key = step.Key;
            }

            return stepResult;
        }

        return new StepResult
        {
            Key = step.Key,
            Status = StepStatus.Succeeded,
            Result = result
        };
    }

    private static async ValueTask<object?> ExecuteGenericHandlerAsync(object handler, IExecutionContext ctx, IFlowDefinition flow, IStepInstance step)
    {
        var genericHandlerInterface = handler
            .GetType()
            .GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStepHandler<>));

        if (genericHandlerInterface is null)
        {
            return null;
        }

        var inputType = genericHandlerInterface.GetGenericArguments()[0];
        var invokeMethod = _invokeGenericHandlerMethod.MakeGenericMethod(inputType);
        var invocation = invokeMethod.Invoke(null, [handler, ctx, flow, step]);
        if (invocation is not ValueTask<object?> valueTask)
        {
            throw new InvalidOperationException($"Expected '{nameof(ValueTask<object>)}' from generic handler invocation.");
        }

        return await valueTask.ConfigureAwait(false);
    }

    private static async ValueTask<object?> InvokeGenericHandlerAsync<TInput>(object handler, IExecutionContext ctx, IFlowDefinition flow, IStepInstance step)
    {
        var typedHandler = (IStepHandler<TInput>)handler;
        TInput? typedInput;

        try
        {
            typedInput = JsonValueConversion.Deserialize<TInput>(step.Inputs, _jsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new StepResult
            {
                Key = step.Key,
                Status = StepStatus.Failed,
                FailedReason = $"Failed to deserialize inputs for step '{step.Key}' (type '{step.Type}') to '{typeof(TInput).FullName}': {ex.Message}"
            };
        }

        var typedStep = new TypedStepInstanceAdapter<TInput>(step, typedInput!);
        var result = await typedHandler.ExecuteAsync(ctx, flow, typedStep).ConfigureAwait(false);
        typedStep.SyncInputsToInner(_jsonOptions);
        return result;
    }
}
