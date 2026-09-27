using System.Threading.Channels;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using NSubstitute;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// InMemory-side coverage for the #189 performance backlog: the step point read, signal-waiter
/// retention, the bounded <see cref="DashboardStatistics.TotalFlows"/> count, and the slim envelope
/// a delayed schedule retains.
/// </summary>
public sealed class InMemoryHotPathAndRetentionTests
{
    // ── IFlowRunStore.GetStepAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetStepAsync_returns_a_copy_of_the_single_step_row()
    {
        // Arrange
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        await store.StartRunAsync(Guid.NewGuid(), "Flow", runId, "manual", null, null);
        await store.RecordStepStartAsync(runId, "a", "Work", null, null);
        await store.RecordStepCompleteAsync(runId, "a", "Failed", null, "boom");

        // Act
        var first = await store.GetStepAsync(runId, "a");
        var second = await store.GetStepAsync(runId, "a");
        var missing = await store.GetStepAsync(runId, "nope");

        // Assert — a snapshot, never the live record the write path mutates in place.
        Assert.NotNull(first);
        Assert.Equal("Failed", first!.Status);
        Assert.Equal("boom", first.ErrorMessage);
        Assert.NotSame(first, second);
        Assert.Null(missing);
    }

    // ── InMemoryFlowSignalStore retention ───────────────────────────────────────

    [Fact]
    public async Task Signal_store_cleanup_drops_waiters_of_purged_runs_and_keeps_live_ones()
    {
        // Arrange — one run old enough to be purged, one still running, each with a waiter.
        var runs = new InMemoryFlowRunStore();
        var signals = new InMemoryFlowSignalStore(runs);
        var purged = Guid.NewGuid();
        var live = Guid.NewGuid();
        await runs.StartRunAsync(Guid.NewGuid(), "Flow", purged, "manual", null, null);
        await runs.CompleteRunAsync(purged, "Succeeded");
        runs.TryMutateRunForTests(purged, r => r.CompletedAt = DateTimeOffset.UtcNow.AddDays(-30));
        await runs.StartRunAsync(Guid.NewGuid(), "Flow", live, "manual", null, null);
        await signals.RegisterWaiterAsync(purged, "wait", "go", null);
        await signals.RegisterWaiterAsync(live, "wait", "go", null);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);

        // Act — the run store purges first, then the signal store follows it.
        await runs.CleanupAsync(cutoff, CancellationToken.None);
        await signals.CleanupAsync(cutoff, CancellationToken.None);

        // Assert
        Assert.Null(await signals.GetWaiterAsync(purged, "wait"));
        Assert.NotNull(await signals.GetWaiterAsync(live, "wait"));
    }

    [Fact]
    public async Task Standalone_signal_store_cleanup_is_a_no_op()
    {
        // Arrange — no paired run store, so there is no way to know which runs are gone.
        var signals = new InMemoryFlowSignalStore();
        var runId = Guid.NewGuid();
        await signals.RegisterWaiterAsync(runId, "wait", "go", null);

        // Act
        await signals.CleanupAsync(DateTimeOffset.UtcNow.AddYears(1), CancellationToken.None);

        // Assert
        Assert.NotNull(await signals.GetWaiterAsync(runId, "wait"));
    }

    // ── DashboardStatistics.TotalFlows ──────────────────────────────────────────

    [Fact]
    public async Task TotalFlows_counts_enabled_definitions_when_a_flow_store_is_paired()
    {
        // Arrange — two definitions (one disabled) and runs for a third, unregistered flow.
        var flows = new InMemoryFlowStore();
        await flows.SaveAsync(new FlowDefinitionRecord { Id = Guid.NewGuid(), Name = "Enabled", IsEnabled = true });
        var disabled = await flows.SaveAsync(new FlowDefinitionRecord { Id = Guid.NewGuid(), Name = "Disabled" });
        await flows.SetEnabledAsync(disabled.Id, false);
        var store = new InMemoryFlowRunStore(flows);
        await store.StartRunAsync(Guid.NewGuid(), "Unregistered", Guid.NewGuid(), "manual", null, null);
        await store.StartRunAsync(Guid.NewGuid(), "Unregistered2", Guid.NewGuid(), "manual", null, null);

        // Act
        var stats = await store.GetStatisticsAsync();

        // Assert — the documented contract, and the same number the SQL backends now report.
        Assert.Equal(1, stats.TotalFlows);
        Assert.Equal(2, stats.ActiveRuns);
    }

    [Fact]
    public async Task TotalFlows_falls_back_to_distinct_run_flows_without_a_flow_store()
    {
        // Arrange
        var store = new InMemoryFlowRunStore();
        var flowId = Guid.NewGuid();
        await store.StartRunAsync(flowId, "Flow", Guid.NewGuid(), "manual", null, null);
        await store.StartRunAsync(flowId, "Flow", Guid.NewGuid(), "manual", null, null);

        // Act
        var stats = await store.GetStatisticsAsync();

        // Assert
        Assert.Equal(1, stats.TotalFlows);
    }

    // ── InMemoryStepDispatcher delayed schedule ─────────────────────────────────

    [Fact]
    public async Task Delayed_schedule_does_not_retain_the_trigger_payload()
    {
        // Arrange — a parked WaitForSignal holds its safety-net schedule for up to 24 hours; the
        // envelope must not keep the resolved trigger payload and headers alive for all of it.
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var sut = new InMemoryStepDispatcher(channel.Writer);
        var payload = new { orderId = "ORD-1" };
        var headers = new Dictionary<string, string> { ["X-Request-Id"] = "r-1" };
        var runId = Guid.NewGuid();
        var ctx = new Core.Execution.ExecutionContext
        {
            RunId = runId,
            PrincipalId = "user-1",
            JobId = "job-1",
            TriggerData = payload,
            TriggerHeaders = headers,
        };
        var step = new StepInstance("wait", "WaitForSignal")
        {
            RunId = runId,
            PrincipalId = "user-1",
            TriggerData = payload,
            TriggerHeaders = headers,
            ScheduledTime = DateTimeOffset.UtcNow,
            Inputs = new Dictionary<string, object?> { ["signalName"] = "go" },
            Index = 3,
            ScopeMoveNext = true,
        };

        // Act
        await sut.ScheduleStepAsync(ctx, Substitute.For<IFlowDefinition>(), step, TimeSpan.FromMilliseconds(1));
        var envelope = await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        // Assert — identity and step shape survive; the payload does not (the engine reloads it).
        Assert.Null(envelope.Context.TriggerData);
        Assert.Null(envelope.Context.TriggerHeaders);
        Assert.Null(envelope.Step.TriggerData);
        Assert.Null(envelope.Step.TriggerHeaders);
        Assert.Equal(runId, envelope.Context.RunId);
        Assert.Equal("user-1", envelope.Context.PrincipalId);
        Assert.Equal("job-1", envelope.Context.JobId);
        Assert.Equal("wait", envelope.Step.Key);
        Assert.Equal("WaitForSignal", envelope.Step.Type);
        Assert.Equal(step.ScheduledTime, envelope.Step.ScheduledTime);
        Assert.Equal("go", envelope.Step.Inputs["signalName"]);
        Assert.Equal(3, envelope.Step.Index);
        Assert.True(envelope.Step.ScopeMoveNext);
    }

    [Fact]
    public async Task Immediate_enqueue_still_hands_over_the_callers_objects()
    {
        // Arrange — an immediate enqueue is consumed at once, so there is nothing to slim.
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var sut = new InMemoryStepDispatcher(channel.Writer);
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid(), TriggerData = new { a = 1 } };
        var step = new StepInstance("s", "Work") { RunId = ctx.RunId };

        // Act
        await sut.EnqueueStepAsync(ctx, Substitute.For<IFlowDefinition>(), step);
        var envelope = await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        Assert.Same(ctx, envelope.Context);
        Assert.Same(step, envelope.Step);
    }
}
