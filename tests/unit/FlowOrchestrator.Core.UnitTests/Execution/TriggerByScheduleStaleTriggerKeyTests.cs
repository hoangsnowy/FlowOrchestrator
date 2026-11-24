using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// <see cref="FlowOrchestratorEngine.TriggerByScheduleAsync"/> for a cron trigger key the flow's manifest no
/// longer declares.
/// </summary>
/// <remarks>
/// <para>
/// Recurring jobs outlive the code that created them: Hangfire keeps <c>flow-{flowId}-{triggerKey}</c> in its
/// storage and a Service Bus tick can already be in the broker. <see cref="RecurringTriggerSync"/> only walks
/// the triggers the CURRENT manifest declares, so renaming or deleting a cron trigger never removes the old
/// recurring job — nothing enumerates the jobs that exist and drops the orphans.
/// </para>
/// <para>
/// The engine is the last line of defence and has none: <c>TriggerByScheduleAsync</c> builds a
/// <see cref="Trigger"/> from whatever key it is handed and starts a full run. On Hangfire the orphaned
/// recurring job therefore keeps starting real runs on the old schedule for ever after the trigger was removed
/// from code.
/// </para>
/// </remarks>
public sealed class TriggerByScheduleStaleTriggerKeyTests
{
    [Fact]
    public async Task TriggerByScheduleAsync_TriggerKeyNotInManifest_DoesNotStartARun()
    {
        // Arrange — the flow's cron trigger was renamed "nightly" -> "daily"; the old recurring job still fires.
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        var manifest = new FlowManifest
        {
            Steps = new StepCollection { ["step1"] = new StepMetadata { Type = "Work" } }
        };
        manifest.Triggers["daily"] = new TriggerMetadata
        {
            Type = TriggerType.Cron,
            Inputs = new Dictionary<string, object?> { ["cronExpression"] = "0 6 * * *" }
        };
        flow.Manifest.Returns(manifest);

        var store = new InMemoryFlowRunStore();
        var dispatcher = Substitute.For<IStepDispatcher>();
        dispatcher.EnqueueStepAsync(default!, default!, default!, default).ReturnsForAnyArgs(new ValueTask<string?>("job"));
        var engine = new FlowOrchestratorEngine(
            dispatcher,
            new FlowGraphPlanner(),
            Substitute.For<IStepExecutor>(),
            Substitute.For<IFlowStore>(),
            store,
            new InMemoryOutputsRepository(store),
            Substitute.For<IExecutionContextAccessor>(),
            new FlowRegistry([flow]),
            [store],
            [store],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            Substitute.For<ILogger<FlowOrchestratorEngine>>(),
            NoopFlowEventNotifier.Instance);

        // Act — the orphaned "flow-{id}-nightly" recurring job fires.
        _ = await Record.ExceptionAsync(async () => await engine.TriggerByScheduleAsync(flowId, "nightly", "hangfire-job-1"));

        // Assert — no run may start for a schedule the flow no longer has.
        var active = await store.GetActiveRunsAsync();
        Assert.True(active.Count == 0,
            $"A schedule for trigger 'nightly', which the manifest no longer declares, started {active.Count} run(s).");
        Assert.Empty(dispatcher.ReceivedCalls());
    }
}
