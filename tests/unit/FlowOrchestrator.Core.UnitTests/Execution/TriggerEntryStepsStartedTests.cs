using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// <see cref="ITriggerContext.EntryStepsStarted"/> tells a caller whose <c>TriggerAsync</c> threw whether the run is
/// alive: <see langword="false"/> until an entry step has been dispatched or skipped, then <see langword="true"/>.
/// The webhook endpoint relies on it to decide whether a publisher's retry may be processed.
/// </summary>
public sealed class TriggerEntryStepsStartedTests
{
    private static IFlowDefinition FlowOf(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = steps
        });
        return flow;
    }

    private static IStepResult Ok(string key) => new StepResult { Key = key, Status = StepStatus.Succeeded };

    private static TriggerContext ContextFor(IFlowDefinition flow) => new()
    {
        RunId = Guid.NewGuid(),
        Flow = flow,
        Trigger = new Trigger("manual", "Manual", null)
    };

    [Fact]
    public async Task Failure_before_any_entry_step_was_dispatched_leaves_the_flag_false()
    {
        // Arrange
        var flow = FlowOf(new StepCollection { ["a"] = new StepMetadata { Type = "Echo" } });
        var harness = new LoopBarrierEngineHarness(flow, Ok);
        harness.FailNextDispatch(new InvalidOperationException("broker unavailable"));
        var ctx = ContextFor(flow);

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await harness.Engine.TriggerAsync(ctx));

        // Assert
        Assert.False(ctx.EntryStepsStarted);
    }

    [Fact]
    public async Task Failure_after_an_entry_step_was_dispatched_sets_the_flag()
    {
        // Arrange — two entry steps; "a" is dispatched, "b" throws.
        var flow = FlowOf(new StepCollection
        {
            ["a"] = new StepMetadata { Type = "Echo" },
            ["b"] = new StepMetadata { Type = "Echo" }
        });
        var harness = new LoopBarrierEngineHarness(flow, Ok);
        harness.FailDispatchOf("b", new InvalidOperationException("broker unavailable"));
        var ctx = ContextFor(flow);

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await harness.Engine.TriggerAsync(ctx));

        // Assert
        Assert.True(ctx.EntryStepsStarted);
    }

    [Fact]
    public async Task Successful_trigger_sets_the_flag()
    {
        // Arrange
        var flow = FlowOf(new StepCollection { ["a"] = new StepMetadata { Type = "Echo" } });
        var harness = new LoopBarrierEngineHarness(flow, Ok);
        var ctx = ContextFor(flow);

        // Act
        await harness.Engine.TriggerAsync(ctx);

        // Assert
        Assert.True(ctx.EntryStepsStarted);
    }

    [Fact]
    public void Default_interface_implementation_reads_false_and_ignores_writes()
    {
        // Arrange — a third-party context that does not implement the member.
        ITriggerContext ctx = new MinimalTriggerContext();

        // Act
        ctx.EntryStepsStarted = true;

        // Assert
        Assert.False(ctx.EntryStepsStarted);
    }

    private sealed class MinimalTriggerContext : ITriggerContext
    {
        public Guid RunId { get; set; }
        public string? PrincipalId { get; set; }
        public object? TriggerData { get; set; }
        public IReadOnlyDictionary<string, string>? TriggerHeaders { get; set; }
        public string? JobId { get; set; }
        public IFlowDefinition Flow { get; set; } = default!;
        public ITrigger Trigger { get; set; } = default!;
    }
}
