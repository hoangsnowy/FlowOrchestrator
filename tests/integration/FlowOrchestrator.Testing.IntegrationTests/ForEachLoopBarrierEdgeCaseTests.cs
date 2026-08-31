using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Testing.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>
/// Edge-case coverage for the ForEach completion barrier introduced for issue #169: nesting,
/// failing and skipped iterations, a waiter that times out, chained loops, and re-running a loop
/// step whose iterations already finished.
/// </summary>
/// <remarks>
/// Every ordering assertion compares persisted <c>StartedAt</c> / <c>CompletedAt</c> stamps of
/// steps that are causally ordered by the barrier, never an elapsed-time bound — a slow CI box
/// changes the durations but never the order.
/// </remarks>
public sealed class ForEachLoopBarrierEdgeCaseTests
{
    private static readonly TimeSpan TerminalTimeout = TimeSpan.FromSeconds(45);

    private static void AssertRanAfter(FlowTestRunResult result, string downstreamKey, string childKeyPrefix)
    {
        var startedAt = result.Steps[downstreamKey].StartedAt;
        Assert.NotNull(startedAt);

        var children = result.Steps
            .Where(kvp => kvp.Key.StartsWith(childKeyPrefix, StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(children);

        foreach (var (key, child) in children)
        {
            Assert.NotNull(child.CompletedAt);
            Assert.True(
                startedAt >= child.CompletedAt,
                $"'{downstreamKey}' started at {startedAt:O} — before '{key}' completed at {child.CompletedAt:O}.");
        }
    }

    [Fact]
    public async Task NestedForEach_settlesInnerThenOuter_beforeTheDownstreamStep()
    {
        // Arrange
        await using var host = await FlowTestHost.For<NestedForEachFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithHandler<ForEachStepHandler>("ForEach")
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(
            body: new { groups = new[] { "g0", "g1", "g2" } },
            timeout: TerminalTimeout);

        // Assert — 3 outer iterations × (1 inner loop + 2 leaves) all terminal, both barrier
        // levels settled, and the downstream step ran after every leaf.
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["outer"].Status);
        for (var outer = 0; outer < 3; outer++)
        {
            Assert.Equal(StepStatus.Succeeded, result.Steps[$"outer.{outer}.inner"].Status);
            for (var inner = 0; inner < 2; inner++)
            {
                Assert.Equal(StepStatus.Succeeded, result.Steps[$"outer.{outer}.inner.{inner}.leaf"].Status);
            }

            // The inner loop step cannot settle before its own leaves.
            var innerLoop = result.Steps[$"outer.{outer}.inner"];
            for (var inner = 0; inner < 2; inner++)
            {
                var leaf = result.Steps[$"outer.{outer}.inner.{inner}.leaf"];
                Assert.True(
                    innerLoop.CompletedAt >= leaf.CompletedAt,
                    $"inner loop {outer} settled at {innerLoop.CompletedAt:O} before leaf {inner} completed at {leaf.CompletedAt:O}.");
            }
        }

        AssertRanAfter(result, "after_outer", "outer.");
    }

    [Fact]
    public async Task FailingIteration_stillSettlesTheLoop_andRunsTheDownstreamStep()
    {
        // Arrange — every iteration's first child throws, blocking its sibling.
        await using var host = await FlowTestHost.For<ForEachFailingChildFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithHandler<BoomStepHandler>("Boom")
            .WithHandler<ForEachStepHandler>("ForEach")
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(
            body: new { items = new[] { "a", "b" } },
            timeout: TerminalTimeout);

        // Assert — Failed and Skipped are terminal, so the barrier settles and the gated step runs.
        Assert.False(result.TimedOut);
        Assert.Equal(StepStatus.Succeeded, result.Steps["loop"].Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["after_loop"].Status);
        for (var index = 0; index < 2; index++)
        {
            Assert.Equal(StepStatus.Failed, result.Steps[$"loop.{index}.boom"].Status);
            Assert.Equal(StepStatus.Skipped, result.Steps[$"loop.{index}.never"].Status);
        }

        AssertRanAfter(result, "after_loop", "loop.");
    }

    [Fact]
    public async Task WhenSkippedIteration_countsAsTerminal_andSettlesTheLoop()
    {
        // Arrange — the loop's second child is gated on a false When clause.
        await using var host = await FlowTestHost.For<ForEachWhenSkipFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithHandler<ForEachStepHandler>("ForEach")
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(
            body: new { amount = 10, items = new[] { "a", "b" } },
            timeout: TerminalTimeout);

        // Assert
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["loop"].Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["after_loop"].Status);
        for (var index = 0; index < 2; index++)
        {
            Assert.Equal(StepStatus.Succeeded, result.Steps[$"loop.{index}.first"].Status);
            Assert.Equal(StepStatus.Skipped, result.Steps[$"loop.{index}.gated"].Status);
        }
    }

    [Fact]
    public async Task WaiterThatTimesOutInsideALoop_settlesTheBarrier_insteadOfStrandingTheRun()
    {
        // Arrange — the loop parks on a signal nobody sends; each waiter expires after 1 s.
        await using var host = await FlowTestHost.For<ForEachSignalTimeoutFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithHandler<ForEachStepHandler>("ForEach")
            .WithFastPolling()
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(
            body: new { items = new[] { "a", "b" } },
            timeout: TerminalTimeout);

        // Assert
        Assert.False(result.TimedOut);
        Assert.Equal(StepStatus.Succeeded, result.Steps["loop"].Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["after_loop"].Status);
        for (var index = 0; index < 2; index++)
        {
            Assert.Equal(StepStatus.Failed, result.Steps[$"loop.{index}.wait"].Status);
        }

        AssertRanAfter(result, "after_loop", "loop.");
    }

    [Fact]
    public async Task ChainedLoops_secondLoopFansOutOnlyAfterTheFirstOneSettled()
    {
        // Arrange
        await using var host = await FlowTestHost.For<SequentialForEachFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithHandler<ForEachStepHandler>("ForEach")
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(
            body: new { items = new[] { "a", "b", "c" } },
            timeout: TerminalTimeout);

        // Assert
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["loop_a"].Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["loop_b"].Status);

        // loop_b's own children must all start after every loop_a child finished.
        var firstBStart = result.Steps
            .Where(kvp => kvp.Key.StartsWith("loop_b.", StringComparison.Ordinal))
            .Min(kvp => kvp.Value.StartedAt);
        var lastAEnd = result.Steps
            .Where(kvp => kvp.Key.StartsWith("loop_a.", StringComparison.Ordinal))
            .Max(kvp => kvp.Value.CompletedAt);
        Assert.NotNull(firstBStart);
        Assert.NotNull(lastAEnd);
        Assert.True(
            firstBStart >= lastAEnd,
            $"loop_b started at {firstBStart:O} — before loop_a finished at {lastAEnd:O}.");

        AssertRanAfter(result, "tail", "loop_b.");
    }

    [Fact]
    public async Task RetryingASettledLoopStep_doesNotStrandTheRunOnItsBarrier()
    {
        // Arrange — a completed run, then an operator re-runs the loop step from the dashboard.
        // RetryStepAsync clears the dispatch ledger for the retried key only, so the re-executed
        // ForEach re-arms the barrier while its children are suppressed as already-dispatched;
        // the loop can only settle again from its own continuation.
        await using var host = await FlowTestHost.For<ForEachTestFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithHandler<ForEachStepHandler>("ForEach")
            .BuildAsync();

        var first = await host.TriggerAsync(
            body: new { items = new[] { "a", "b" } },
            timeout: TerminalTimeout);
        Assert.Equal(RunStatus.Succeeded, first.Status);

        // Act
        using (var scope = host.Services.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IFlowOrchestrator>();
            var flow = scope.ServiceProvider.GetServices<IFlowDefinition>().OfType<ForEachTestFlow>().First();
            await orchestrator.RetryStepAsync(flow.Id, first.RunId, "process_items");
        }

        var result = await host.WaitForRunAsync(first.RunId, TerminalTimeout);

        // Assert — the run reaches a terminal state again and the loop is not left Running.
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["process_items"].Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["finalize"].Status);
    }

    [Fact]
    public async Task LoopStepIsRunningWhileItsIterationsAreInFlight_andCountsAsInFlightWork()
    {
        // Arrange — a single parked iteration is enough to prove the loop is a live step, not a
        // completed one: the run must stay Running rather than being classified terminal.
        await using var host = await FlowTestHost.For<ForEachLoopBarrierFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithHandler<ForEachStepHandler>("ForEach")
            .WithFastPolling()
            .BuildAsync();

        using var scope = host.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IFlowOrchestrator>();
        var flow = scope.ServiceProvider.GetServices<IFlowDefinition>().OfType<ForEachLoopBarrierFlow>().First();
        var body = new Dictionary<string, object?> { ["Steps"] = new[] { "only-one" } };
        var ctx = new TriggerContext
        {
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", body),
            RunId = Guid.Empty,
            TriggerData = body
        };

        // Act
        await orchestrator.TriggerAsync(ctx);
        var signalStore = host.Services.GetRequiredService<IFlowSignalStore>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15)
               && await signalStore.GetWaiterAsync(ctx.RunId, "scan_process.0.wait_robot_goto") is null)
        {
            await Task.Delay(25);
        }

        // Assert
        var runStore = host.Services.GetRequiredService<IFlowRunStore>();
        var detail = await runStore.GetRunDetailAsync(ctx.RunId);
        Assert.NotNull(detail);
        Assert.Equal("Running", detail!.Status);
        var loopStep = detail.Steps!.Single(step => step.StepKey == "scan_process");
        Assert.Equal(nameof(StepStatus.Running), loopStep.Status);
        Assert.Null(loopStep.CompletedAt);
        Assert.DoesNotContain(detail.Steps!, step => step.StepKey == "robot_callback_success");

        // Cleanup — release the waiter so the host disposes without a parked run.
        var signals = host.Services.GetRequiredService<IFlowSignalDispatcher>();
        await signals.DispatchAsync(ctx.RunId, "robot_goto", """{"Location":"BAY-A"}""");
        await host.WaitForRunAsync(ctx.RunId, TerminalTimeout);
    }
}
