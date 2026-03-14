using FlowOrchestrator.Core.Execution;
using Hangfire.Server;

namespace FlowOrchestrator.Hangfire;

public interface IHangfireFlowTrigger
{
    ValueTask<object?> TriggerAsync(ITriggerContext triggerContext, PerformContext? performContext = null);
}
