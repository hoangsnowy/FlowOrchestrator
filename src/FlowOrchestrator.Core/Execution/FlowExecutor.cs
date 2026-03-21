using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Execution;

public sealed class FlowExecutor : IFlowExecutor
{
    private readonly IOutputsRepository _outputsRepository;

    public FlowExecutor(IOutputsRepository outputsRepository)
    {
        _outputsRepository = outputsRepository;
    }

    public async ValueTask<IStepInstance> TriggerFlow(ITriggerContext context)
    {
        context.TriggerData = context.Trigger.Data;
        await _outputsRepository.SaveTriggerDataAsync(context, context.Flow, context.Trigger).ConfigureAwait(false);

        var steps = context.Flow.Manifest.Steps;

        // First step: no RunAfter or empty RunAfter.
        var first = steps.FirstOrDefault(kvp => kvp.Value is { } meta && (meta.RunAfter is null || meta.RunAfter.Count == 0));
        if (first.Key is null)
        {
            throw new InvalidOperationException("No entry step found for flow.");
        }

        var instance = new StepInstance(first.Key, first.Value.Type)
        {
            RunId = context.RunId,
            PrincipalId = context.PrincipalId,
            TriggerData = context.TriggerData,
            ScheduledTime = DateTimeOffset.UtcNow,
            Inputs = new Dictionary<string, object?>(first.Value.Inputs)
        };

        return instance;
    }

    public ValueTask<IStepInstance?> GetNextStep(IExecutionContext context, IFlowDefinition flow, IStepInstance currentStep, IStepResult result)
    {
        var steps = flow.Manifest.Steps;

        var nextMetadata = steps.FindNextStep(currentStep.Key);
        if (nextMetadata is null)
        {
            return ValueTask.FromResult<IStepInstance?>(null);
        }

        var nextKey = steps.First(kvp => ReferenceEquals(kvp.Value, nextMetadata)).Key;
        if (!nextMetadata.ShouldExecute(currentStep.Key, result.Status))
        {
            return ValueTask.FromResult<IStepInstance?>(null);
        }

        var instance = new StepInstance(nextKey, nextMetadata.Type)
        {
            RunId = context.RunId,
            PrincipalId = context.PrincipalId,
            TriggerData = context.TriggerData,
            ScheduledTime = DateTimeOffset.UtcNow,
            Inputs = new Dictionary<string, object?>(nextMetadata.Inputs)
        };

        return ValueTask.FromResult<IStepInstance?>(instance);
    }
}
