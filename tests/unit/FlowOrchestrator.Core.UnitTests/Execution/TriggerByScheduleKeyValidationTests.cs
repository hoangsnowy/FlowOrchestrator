using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// The other half of <see cref="TriggerByScheduleStaleTriggerKeyTests"/>: a schedule for a cron trigger the
/// manifest declares still starts a run, and a schedule naming a declared but non-cron trigger is rejected.
/// </summary>
public sealed class TriggerByScheduleKeyValidationTests
{
    private static IFlowDefinition FlowWithTriggers()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["daily"] = new TriggerMetadata
                {
                    Type = TriggerType.Cron,
                    Inputs = new Dictionary<string, object?> { ["cronExpression"] = "0 6 * * *" }
                },
                ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
            },
            Steps = new StepCollection { ["a"] = new StepMetadata { Type = "Echo" } }
        });
        return flow;
    }

    [Fact]
    public async Task TriggerByScheduleAsync_DeclaredCronTrigger_StartsARun()
    {
        // Arrange
        var flow = FlowWithTriggers();
        var harness = new LoopBarrierEngineHarness(flow, key => new StepResult { Key = key, Status = StepStatus.Succeeded });

        // Act
        var result = await harness.Engine.TriggerByScheduleAsync(flow.Id, "daily", "job-1") as FlowTriggerResult;

        // Assert
        Assert.NotNull(result?.RunId);
        Assert.Equal(["a"], harness.Enqueued);
    }

    [Fact]
    public async Task TriggerByScheduleAsync_DeclaredButNotCronTrigger_IsRejected()
    {
        // Arrange
        var flow = FlowWithTriggers();
        var harness = new LoopBarrierEngineHarness(flow, key => new StepResult { Key = key, Status = StepStatus.Succeeded });

        // Act
        var result = await harness.Engine.TriggerByScheduleAsync(flow.Id, "manual", "job-1") as FlowTriggerResult;

        // Assert
        Assert.NotNull(result);
        Assert.Null(result.RunId);
        Assert.Empty(harness.Enqueued);
        Assert.Empty(await harness.Store.GetActiveRunsAsync());
    }
}
