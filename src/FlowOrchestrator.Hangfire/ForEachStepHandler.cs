using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using Hangfire;

namespace FlowOrchestrator.Hangfire;

public sealed class ForEachStepHandler : IStepHandler
{
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly IFlowExecutor _flowExecutor;

    public ForEachStepHandler(IBackgroundJobClient backgroundJobClient, IFlowExecutor flowExecutor)
    {
        _backgroundJobClient = backgroundJobClient;
        _flowExecutor = flowExecutor;
    }

    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
    {
        if (flow.Manifest.Steps.FindStep(step.Key) is not LoopStepMetadata loopMetadata)
        {
            return ValueTask.FromResult<object?>(null);
        }

        if (loopMetadata.ForEach is not IEnumerable<object?> items)
        {
            return ValueTask.FromResult<object?>(null);
        }

        var index = 0;
        foreach (var _ in items)
        {
            var childKey = $"{step.Key}.0";
            var childInstance = new StepInstance(childKey, loopMetadata.Steps.First().Value.Type)
            {
                RunId = context.RunId,
                PrincipalId = context.PrincipalId,
                Index = index++
            };

            _backgroundJobClient.Enqueue<IHangfireStepRunner>(runner => runner.RunStepAsync(context, flow, childInstance, null));
        }

        return ValueTask.FromResult<object?>(null);
    }
}

