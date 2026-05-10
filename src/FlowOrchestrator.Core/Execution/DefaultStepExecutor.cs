using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Default implementation of <see cref="IStepExecutor"/> that resolves the matching
/// <see cref="IStepHandlerMetadata"/> by type name, evaluates <c>@triggerBody()</c>,
/// <c>@triggerHeaders()</c>, and <c>@steps()</c> input expressions via the
/// <see cref="InputResolutionPipeline"/> + <see cref="StepExpressionResolutionPipeline"/>,
/// and delegates execution to the registered handler.
/// </summary>
public sealed class DefaultStepExecutor : IStepExecutor
{
    private readonly IEnumerable<IStepHandlerMetadata> _handlerMetadata;
    private readonly IServiceProvider _serviceProvider;
    private readonly IOutputsRepository _outputsRepository;
    private readonly IFlowRunStore _runStore;

    /// <summary>
    /// Constructs the executor with the registered step handler metadata, service provider,
    /// outputs repository, and run store.
    /// </summary>
    public DefaultStepExecutor(
        IEnumerable<IStepHandlerMetadata> handlerMetadata,
        IServiceProvider serviceProvider,
        IOutputsRepository outputsRepository,
        IFlowRunStore runStore)
    {
        _handlerMetadata = handlerMetadata;
        _serviceProvider = serviceProvider;
        _outputsRepository = outputsRepository;
        _runStore = runStore;
    }

    /// <summary>
    /// Resolves inputs (trigger and step-output expressions), saves them to the output store,
    /// then invokes the handler registered for <paramref name="step"/>'s type.
    /// Returns <see cref="StepStatus.Skipped"/> if the step metadata or its handler cannot be found.
    /// </summary>
    /// <param name="context">The execution context for the current run.</param>
    /// <param name="flow">The flow definition that owns this step.</param>
    /// <param name="step">The step instance with pre-resolved inputs.</param>
    public async ValueTask<IStepResult> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
    {
        var metadata = flow.Manifest.Steps.FindStep(step.Key);
        if (metadata is null)
        {
            return new StepResult
            {
                Key = step.Key,
                Status = StepStatus.Skipped,
                FailedReason = "Step metadata not found."
            };
        }

        // Pass 1 (sync): resolve @triggerBody() and @triggerHeaders() expressions.
        step.Inputs = InputResolutionPipeline.Resolve(step.Inputs, context.TriggerData, context.TriggerHeaders);

        // Pass 2 (async): resolve @steps('key').output|status|error expressions.
        var resolver = new StepOutputResolver(_outputsRepository, _runStore, context.RunId, flow.Manifest.Steps);
        step.Inputs = await StepExpressionResolutionPipeline.ResolveAsync(step.Inputs, resolver).ConfigureAwait(false);

        await _outputsRepository.SaveStepInputAsync(context, flow, step).ConfigureAwait(false);

        var handler = _handlerMetadata.FirstOrDefault(h => string.Equals(h.Type, metadata.Type, StringComparison.OrdinalIgnoreCase));
        if (handler is null)
        {
            return new StepResult
            {
                Key = step.Key,
                Status = StepStatus.Skipped,
                FailedReason = $"No handler registered for type '{metadata.Type}'."
            };
        }

        return await handler.ExecuteAsync(_serviceProvider, context, flow, step).ConfigureAwait(false);
    }
}
