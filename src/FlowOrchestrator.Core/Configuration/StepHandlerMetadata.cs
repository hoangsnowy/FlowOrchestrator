using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Core.Configuration;

internal sealed class StepHandlerMetadata<THandler> : IStepHandlerMetadata
    where THandler : class
{
    public StepHandlerMetadata(string type)
    {
        Type = type;
    }

    public string Type { get; }

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
                result = null;
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
}
