using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using Hangfire.Server;

namespace FlowOrchestrator.Hangfire;

public interface IHangfireStepRunner
{
    ValueTask<object?> RunStepAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, PerformContext? performContext = null);
    ValueTask<object?> RetryStepAsync(Guid flowId, Guid runId, string stepKey, PerformContext? performContext = null);
}
