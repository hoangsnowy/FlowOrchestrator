using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// RFC 2.0 S7: <c>TriggerAsync</c> registers the idempotency key before it creates the run, so a process killed
/// in between left the key pointing at a run that never started work and every retry of the delivery was
/// answered "duplicate" of that dead run. A duplicate now takes over a reservation older than
/// <see cref="FlowRunControlOptions.IdempotencyReservationTimeout"/> whose run has no dispatch-ledger and no
/// step row; anything else is still a duplicate.
/// </summary>
/// <remarks>
/// The kill is modelled by writing what the dead trigger had committed (the key, optionally the run row)
/// straight into the store; time is moved with the store's clock seam, never waited for.
/// </remarks>
public sealed class TriggerIdempotencyTakeoverTests
{
    private const string Key = "order-1";
    private static readonly Dictionary<string, string> Headers = new() { ["Idempotency-Key"] = Key };
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    private static IFlowDefinition FlowOf()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = new StepCollection { ["a"] = new StepMetadata { Type = "Echo" } }
        });
        return flow;
    }

    private static IStepResult Ok(string key) => new StepResult { Key = key, Status = StepStatus.Succeeded };

    private static TriggerContext ContextFor(IFlowDefinition flow, Guid runId) => new()
    {
        RunId = runId,
        Flow = flow,
        Trigger = new Trigger("manual", "Manual", null, headers: Headers)
    };

    /// <summary>
    /// Leaves the store as a trigger killed after <c>StartRunAsync</c> leaves it: the key registered at
    /// <see cref="T0"/> and a <c>Running</c> run row with no step, control or ledger row.
    /// </summary>
    private static async Task<(LoopBarrierEngineHarness Harness, ManualClock Clock, Guid DeadRunId)> KilledTriggerAsync(
        IFlowDefinition flow,
        FlowRunControlOptions? options = null,
        Func<InMemoryFlowRunStore, IFlowRunControlStore>? controlStore = null)
    {
        var harness = new LoopBarrierEngineHarness(flow, Ok, runControlOptions: options, controlStore: controlStore);
        var clock = new ManualClock(T0);
        harness.Store.Clock = clock;
        var deadRunId = Guid.NewGuid();
        Assert.True(await harness.Store.TryRegisterIdempotencyKeyAsync(flow.Id, "manual", Key, deadRunId));
        await harness.Store.StartRunAsync(flow.Id, "Flow", deadRunId, "manual", null, null);
        return (harness, clock, deadRunId);
    }

    [Fact]
    public async Task StaleReservationOfAnUnstartedRun_IsTakenOver_AndTheRetryRuns()
    {
        // Arrange
        var flow = FlowOf();
        var (harness, clock, deadRunId) = await KilledTriggerAsync(flow);
        clock.Advance(TimeSpan.FromSeconds(31));
        var retryRunId = Guid.NewGuid();

        // Act
        var retry = await harness.Engine.TriggerAsync(ContextFor(flow, retryRunId)) as FlowTriggerResult;
        await harness.DrainAsync();

        // Assert
        Assert.NotNull(retry);
        Assert.False(retry.Duplicate);
        Assert.Equal(retryRunId, retry.RunId);
        Assert.Equal("Succeeded", await harness.RunStatusAsync(retryRunId));
        Assert.Equal(retryRunId, await harness.Store.FindRunIdByIdempotencyKeyAsync(flow.Id, "manual", Key));
        Assert.Equal("Failed", await harness.RunStatusAsync(deadRunId));
        var deadControl = await harness.Store.GetRunControlAsync(deadRunId);
        Assert.NotNull(deadControl);
        Assert.True(deadControl.CancelRequested);
        Assert.Contains(retryRunId.ToString(), deadControl.CancelReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReservationYoungerThanTheTimeout_IsAnsweredAsDuplicate()
    {
        // Arrange
        var flow = FlowOf();
        var (harness, clock, deadRunId) = await KilledTriggerAsync(flow);
        clock.Advance(TimeSpan.FromSeconds(29));

        // Act
        var retry = await harness.Engine.TriggerAsync(ContextFor(flow, Guid.NewGuid())) as FlowTriggerResult;

        // Assert
        Assert.NotNull(retry);
        Assert.True(retry.Duplicate);
        Assert.Equal(deadRunId, retry.RunId);
        Assert.Equal(deadRunId, await harness.Store.FindRunIdByIdempotencyKeyAsync(flow.Id, "manual", Key));
        Assert.Empty(harness.Enqueued);
        Assert.Equal("Running", await harness.RunStatusAsync(deadRunId));
    }

    [Fact]
    public async Task ConfiguredTimeout_IsTheOneApplied()
    {
        // Arrange - 61 s is stale under the 30 s default but not under the configured 2 minutes.
        var flow = FlowOf();
        var options = new FlowRunControlOptions { IdempotencyReservationTimeout = TimeSpan.FromMinutes(2) };
        var (harness, clock, deadRunId) = await KilledTriggerAsync(flow, options);
        clock.Advance(TimeSpan.FromSeconds(61));

        // Act
        var retry = await harness.Engine.TriggerAsync(ContextFor(flow, Guid.NewGuid())) as FlowTriggerResult;

        // Assert
        Assert.NotNull(retry);
        Assert.True(retry.Duplicate);
        Assert.Equal(deadRunId, retry.RunId);
        Assert.Empty(harness.Enqueued);
    }

    [Theory]
    [InlineData("ledger")]
    [InlineData("step")]
    public async Task StaleReservationOfARunThatStartedWork_IsAnsweredAsDuplicate(string evidence)
    {
        // Arrange - the first trigger got as far as dispatching (ledger row) or recording (step row) its entry.
        var flow = FlowOf();
        var (harness, clock, liveRunId) = await KilledTriggerAsync(flow);
        if (evidence == "ledger")
        {
            Assert.True(await harness.Store.TryRecordDispatchAsync(liveRunId, "a"));
        }
        else
        {
            await harness.Store.RecordStepStartAsync(liveRunId, "a", "Echo", null, null);
        }

        clock.Advance(TimeSpan.FromMinutes(5));

        // Act
        var retry = await harness.Engine.TriggerAsync(ContextFor(flow, Guid.NewGuid())) as FlowTriggerResult;

        // Assert
        Assert.NotNull(retry);
        Assert.True(retry.Duplicate);
        Assert.Equal(liveRunId, retry.RunId);
        Assert.Equal(liveRunId, await harness.Store.FindRunIdByIdempotencyKeyAsync(flow.Id, "manual", Key));
        Assert.Empty(harness.Enqueued);
        Assert.Equal("Running", await harness.RunStatusAsync(liveRunId));
        Assert.Null(await harness.Store.GetRunControlAsync(liveRunId));
    }

    [Fact]
    public async Task TakeoverLostToAConcurrentRetry_IsAnsweredAsDuplicateOfTheWinner()
    {
        // Arrange - another retry takes the key over between this trigger's read and its own takeover.
        var flow = FlowOf();
        var winnerRunId = Guid.NewGuid();
        var (harness, clock, deadRunId) = await KilledTriggerAsync(
            flow, controlStore: inner => new CompetingTakeoverControlStore(inner, winnerRunId));
        clock.Advance(TimeSpan.FromSeconds(31));

        // Act
        var retry = await harness.Engine.TriggerAsync(ContextFor(flow, Guid.NewGuid())) as FlowTriggerResult;

        // Assert
        Assert.NotNull(retry);
        Assert.True(retry.Duplicate);
        Assert.Equal(winnerRunId, retry.RunId);
        Assert.Equal(winnerRunId, await harness.Store.FindRunIdByIdempotencyKeyAsync(flow.Id, "manual", Key));
        Assert.Empty(harness.Enqueued);
        Assert.True((await harness.Store.GetRunControlAsync(deadRunId))?.CancelRequested);
    }

    [Fact]
    public async Task ConcurrentRetriesOfAStaleReservation_StartExactlyOneRun()
    {
        // Arrange
        const int retries = 16;
        var flow = FlowOf();
        var (harness, clock, deadRunId) = await KilledTriggerAsync(flow);
        clock.Advance(TimeSpan.FromSeconds(31));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new Task<FlowTriggerResult>[retries];
        for (var i = 0; i < retries; i++)
        {
            attempts[i] = Task.Run(async () =>
            {
                await gate.Task;
                return (FlowTriggerResult)(await harness.Engine.TriggerAsync(ContextFor(flow, Guid.NewGuid())))!;
            });
        }

        // Act
        gate.SetResult();
        var results = await Task.WhenAll(attempts);
        await harness.DrainAsync();

        // Assert
        var started = Assert.Single(results, r => !r.Duplicate);
        Assert.All(results.Where(r => r.Duplicate), r => Assert.Equal(started.RunId, r.RunId));
        Assert.Equal(started.RunId, await harness.Store.FindRunIdByIdempotencyKeyAsync(flow.Id, "manual", Key));
        Assert.Equal(["a"], harness.Enqueued);
        Assert.Equal("Succeeded", await harness.RunStatusAsync(started.RunId!.Value));
        Assert.Equal("Failed", await harness.RunStatusAsync(deadRunId));
    }

    [Fact]
    public async Task StepTheStalledTriggerDispatchesAfterTheTakeover_IsSkipped()
    {
        // Arrange - the first trigger was only slow: its entry step is delivered after the takeover.
        var flow = FlowOf();
        var (harness, clock, staleRunId) = await KilledTriggerAsync(flow);
        clock.Advance(TimeSpan.FromSeconds(31));
        var retry = await harness.Engine.TriggerAsync(ContextFor(flow, Guid.NewGuid())) as FlowTriggerResult;
        Assert.NotNull(retry);
        Assert.False(retry.Duplicate);

        // Act
        await harness.RedeliverAsync(staleRunId, "a", "Echo");

        // Assert
        Assert.Equal("Skipped", await harness.StepStatusAsync(staleRunId, "a"));
        Assert.Equal("Failed", await harness.RunStatusAsync(staleRunId));
    }

    [Fact]
    public void IdempotencyReservationTimeout_DefaultsToThirtySeconds()
    {
        // Arrange
        var options = new FlowRunControlOptions();

        // Act
        var timeout = options.IdempotencyReservationTimeout;

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(30), timeout);
        Assert.Equal(FlowRunControlOptions.DefaultIdempotencyReservationTimeout, timeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void IdempotencyReservationTimeout_RejectsANonPositiveValue(int seconds)
    {
        // Arrange
        var options = new FlowRunControlOptions();

        // Act
        var thrown = Record.Exception(() => options.IdempotencyReservationTimeout = TimeSpan.FromSeconds(seconds));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(thrown);
        Assert.Equal(FlowRunControlOptions.DefaultIdempotencyReservationTimeout, options.IdempotencyReservationTimeout);
    }

    /// <summary>Clock moved by the test only.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>
    /// Control-store decorator whose takeover first lets a competing retry (<paramref name="winnerRunId"/>)
    /// take the key over, then forwards the engine's own attempt, which must lose.
    /// </summary>
    private sealed class CompetingTakeoverControlStore(InMemoryFlowRunStore inner, Guid winnerRunId) : IFlowRunControlStore
    {
        public Task ConfigureRunAsync(Guid runId, Guid flowId, string triggerKey, string? idempotencyKey, DateTimeOffset? timeoutAtUtc)
            => inner.ConfigureRunAsync(runId, flowId, triggerKey, idempotencyKey, timeoutAtUtc);

        public Task<FlowRunControlRecord?> GetRunControlAsync(Guid runId) => inner.GetRunControlAsync(runId);

        public Task<bool> RequestCancelAsync(Guid runId, string? reason) => inner.RequestCancelAsync(runId, reason);

        public Task<bool> MarkTimedOutAsync(Guid runId, string? reason) => inner.MarkTimedOutAsync(runId, reason);

        public Task<bool> ExtendDeadlineAsync(Guid runId, DateTimeOffset? newTimeoutAtUtc) => inner.ExtendDeadlineAsync(runId, newTimeoutAtUtc);

        public Task<Guid?> FindRunIdByIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey)
            => inner.FindRunIdByIdempotencyKeyAsync(flowId, triggerKey, idempotencyKey);

        public Task<bool> TryRegisterIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey, Guid runId)
            => inner.TryRegisterIdempotencyKeyAsync(flowId, triggerKey, idempotencyKey, runId);

        public Task<bool> ReleaseIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey, Guid runId)
            => inner.ReleaseIdempotencyKeyAsync(flowId, triggerKey, idempotencyKey, runId);

        public async Task<Guid?> TryTakeOverStaleIdempotencyKeyAsync(
            Guid flowId, string triggerKey, string idempotencyKey, Guid staleRunId, Guid newRunId, TimeSpan reservationTimeout)
        {
            var competitor = await inner.TryTakeOverStaleIdempotencyKeyAsync(
                flowId, triggerKey, idempotencyKey, staleRunId, winnerRunId, reservationTimeout);
            Assert.Equal(winnerRunId, competitor);
            return await inner.TryTakeOverStaleIdempotencyKeyAsync(
                flowId, triggerKey, idempotencyKey, staleRunId, newRunId, reservationTimeout);
        }
    }
}
