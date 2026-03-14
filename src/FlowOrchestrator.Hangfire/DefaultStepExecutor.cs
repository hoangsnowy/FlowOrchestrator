using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Hangfire;

internal sealed class DefaultStepExecutor : IStepExecutor
{
    private readonly IEnumerable<IStepHandlerMetadata> _handlerMetadata;
    private readonly IServiceProvider _serviceProvider;
    private readonly IOutputsRepository _outputsRepository;

    public DefaultStepExecutor(
        IEnumerable<IStepHandlerMetadata> handlerMetadata,
        IServiceProvider serviceProvider,
        IOutputsRepository outputsRepository)
    {
        _handlerMetadata = handlerMetadata;
        _serviceProvider = serviceProvider;
        _outputsRepository = outputsRepository;
    }

    public async ValueTask<IStepResult> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
    {
        var metadata = flow.Manifest.Steps.FindStep(step.Key);
        if (metadata is null)
        {
            return new StepResult
            {
                Key = step.Key,
                Status = "Skipped",
                FailedReason = "Step metadata not found."
            };
        }

        await _outputsRepository.SaveStepInputAsync(context, flow, step).ConfigureAwait(false);

        var handler = _handlerMetadata.FirstOrDefault(h => string.Equals(h.Type, metadata.Type, StringComparison.OrdinalIgnoreCase));
        if (handler is null)
        {
            return new StepResult
            {
                Key = step.Key,
                Status = "Skipped",
                FailedReason = $"No handler registered for type '{metadata.Type}'."
            };
        }

        return await handler.ExecuteAsync(_serviceProvider, context, flow, step).ConfigureAwait(false);
    }
}
