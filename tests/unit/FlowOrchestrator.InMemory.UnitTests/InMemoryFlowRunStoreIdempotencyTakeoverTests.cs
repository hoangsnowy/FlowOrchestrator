using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// <see cref="InMemoryFlowRunStore.TryTakeOverStaleIdempotencyKeyAsync"/>: a registration is taken over only
/// when it still maps to the stale run, is older than the timeout on the store's clock, and its run has no
/// dispatch-ledger and no step row; one of many concurrent callers wins, and the stale run is cancel-latched.
/// </summary>
public sealed class InMemoryFlowRunStoreIdempotencyTakeoverTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryFlowRunStore _sut;
    private readonly Guid _flowId = Guid.NewGuid();
    private readonly Guid _staleRunId = Guid.NewGuid();

    /// <summary>Puts the store on a clock the tests move by hand.</summary>
    public InMemoryFlowRunStoreIdempotencyTakeoverTests()
    {
        _sut = new InMemoryFlowRunStore { Clock = _clock };
    }

    [Theory]
    [InlineData(29_999, false)]
    [InlineData(30_000, true)]
    [InlineData(600_000, true)]
    public async Task Age_is_judged_on_the_store_clock(int ageMs, bool takenOver)
    {
        // Arrange
        Assert.True(await _sut.TryRegisterIdempotencyKeyAsync(_flowId, "webhook", "order-1", _staleRunId));
        _clock.Advance(TimeSpan.FromMilliseconds(ageMs));
        var newRunId = Guid.NewGuid();

        // Act
        var owner = await _sut.TryTakeOverStaleIdempotencyKeyAsync(_flowId, "webhook", "order-1", _staleRunId, newRunId, Timeout);

        // Assert
        var expected = takenOver ? newRunId : _staleRunId;
        Assert.Equal(expected, owner);
        Assert.Equal(expected, await _sut.FindRunIdByIdempotencyKeyAsync(_flowId, "webhook", "order-1"));
        Assert.Equal(takenOver, (await _sut.GetRunControlAsync(_staleRunId))?.CancelRequested ?? false);
    }

    [Theory]
    [InlineData("ledger")]
    [InlineData("step")]
    public async Task A_run_that_started_work_keeps_its_key(string evidence)
    {
        // Arrange
        Assert.True(await _sut.TryRegisterIdempotencyKeyAsync(_flowId, "webhook", "order-1", _staleRunId));
        if (evidence == "ledger")
        {
            Assert.True(await _sut.TryRecordDispatchAsync(_staleRunId, "a"));
        }
        else
        {
            await _sut.RecordStepStartAsync(_staleRunId, "a", "Echo", null, null);
        }

        _clock.Advance(TimeSpan.FromMinutes(10));

        // Act
        var owner = await _sut.TryTakeOverStaleIdempotencyKeyAsync(_flowId, "webhook", "order-1", _staleRunId, Guid.NewGuid(), Timeout);

        // Assert
        Assert.Equal(_staleRunId, owner);
        Assert.Equal(_staleRunId, await _sut.FindRunIdByIdempotencyKeyAsync(_flowId, "webhook", "order-1"));
        Assert.Null(await _sut.GetRunControlAsync(_staleRunId));
    }

    [Fact]
    public async Task A_released_ledger_row_is_not_work()
    {
        // Arrange - a dispatch that threw released its ledger row (the failed-dispatch compensation).
        Assert.True(await _sut.TryRegisterIdempotencyKeyAsync(_flowId, "webhook", "order-1", _staleRunId));
        Assert.True(await _sut.TryRecordDispatchAsync(_staleRunId, "a"));
        await _sut.ReleaseDispatchAsync(_staleRunId, "a");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var newRunId = Guid.NewGuid();

        // Act
        var owner = await _sut.TryTakeOverStaleIdempotencyKeyAsync(_flowId, "webhook", "order-1", _staleRunId, newRunId, Timeout);

        // Assert
        Assert.Equal(newRunId, owner);
    }

    [Fact]
    public async Task A_key_owned_by_another_run_is_not_taken_over()
    {
        // Arrange
        var otherRunId = Guid.NewGuid();
        Assert.True(await _sut.TryRegisterIdempotencyKeyAsync(_flowId, "webhook", "order-1", otherRunId));
        _clock.Advance(TimeSpan.FromMinutes(1));

        // Act
        var owner = await _sut.TryTakeOverStaleIdempotencyKeyAsync(_flowId, "webhook", "order-1", _staleRunId, Guid.NewGuid(), Timeout);

        // Assert
        Assert.Equal(otherRunId, owner);
        Assert.Null(await _sut.GetRunControlAsync(_staleRunId));
    }

    [Fact]
    public async Task A_missing_key_answers_null()
    {
        // Arrange - nothing registered.

        // Act
        var owner = await _sut.TryTakeOverStaleIdempotencyKeyAsync(_flowId, "webhook", "order-1", _staleRunId, Guid.NewGuid(), Timeout);

        // Assert
        Assert.Null(owner);
    }

    [Fact]
    public async Task Keys_are_matched_in_their_normalised_form_and_the_new_reservation_is_restamped()
    {
        // Arrange
        Assert.True(await _sut.TryRegisterIdempotencyKeyAsync(_flowId, "webhook", "Order-1", _staleRunId));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var newRunId = Guid.NewGuid();

        // Act
        var owner = await _sut.TryTakeOverStaleIdempotencyKeyAsync(_flowId, " WEBHOOK ", " order-1 ", _staleRunId, newRunId, Timeout);
        var secondTakeover = await _sut.TryTakeOverStaleIdempotencyKeyAsync(_flowId, "webhook", "order-1", newRunId, Guid.NewGuid(), Timeout);

        // Assert - the new owner got a fresh window, so an immediate takeover of it is refused.
        Assert.Equal(newRunId, owner);
        Assert.Equal(newRunId, secondTakeover);
    }

    [Fact]
    public async Task The_stale_run_is_cancel_latched_keeping_an_earlier_cancel_reason()
    {
        // Arrange - one stale run with a control record and a recorded user cancel, one with none at all.
        var cancelledRunId = Guid.NewGuid();
        Assert.True(await _sut.TryRegisterIdempotencyKeyAsync(_flowId, "webhook", "order-1", _staleRunId));
        Assert.True(await _sut.TryRegisterIdempotencyKeyAsync(_flowId, "webhook", "order-2", cancelledRunId));
        await _sut.ConfigureRunAsync(cancelledRunId, _flowId, "webhook", "order-2", null);
        Assert.True(await _sut.RequestCancelAsync(cancelledRunId, "operator"));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var newRunId = Guid.NewGuid();

        // Act
        await _sut.TryTakeOverStaleIdempotencyKeyAsync(_flowId, "webhook", "order-1", _staleRunId, newRunId, Timeout);
        await _sut.TryTakeOverStaleIdempotencyKeyAsync(_flowId, "webhook", "order-2", cancelledRunId, Guid.NewGuid(), Timeout);

        // Assert
        var created = await _sut.GetRunControlAsync(_staleRunId);
        Assert.NotNull(created);
        Assert.True(created.CancelRequested);
        Assert.Equal(IdempotencyKeyStorage.TakenOverCancelReason(newRunId), created.CancelReason);
        Assert.Equal(_flowId, created.FlowId);
        Assert.Null(created.TimeoutAtUtc);
        var kept = await _sut.GetRunControlAsync(cancelledRunId);
        Assert.NotNull(kept);
        Assert.True(kept.CancelRequested);
        Assert.Equal("operator", kept.CancelReason);
    }

    [Fact]
    public async Task The_stale_trigger_cannot_release_the_key_it_lost()
    {
        // Arrange
        Assert.True(await _sut.TryRegisterIdempotencyKeyAsync(_flowId, "webhook", "order-1", _staleRunId));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var newRunId = Guid.NewGuid();
        await _sut.TryTakeOverStaleIdempotencyKeyAsync(_flowId, "webhook", "order-1", _staleRunId, newRunId, Timeout);

        // Act
        var released = await _sut.ReleaseIdempotencyKeyAsync(_flowId, "webhook", "order-1", _staleRunId);

        // Assert
        Assert.False(released);
        Assert.Equal(newRunId, await _sut.FindRunIdByIdempotencyKeyAsync(_flowId, "webhook", "order-1"));
    }

    [Fact]
    public async Task Sixteen_concurrent_takeovers_have_exactly_one_winner()
    {
        // Arrange
        const int contenders = 16;
        Assert.True(await _sut.TryRegisterIdempotencyKeyAsync(_flowId, "webhook", "order-1", _staleRunId));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var candidates = Enumerable.Range(0, contenders).Select(_ => Guid.NewGuid()).ToArray();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = candidates.Select(candidate => Task.Run(async () =>
        {
            await gate.Task;
            return await _sut.TryTakeOverStaleIdempotencyKeyAsync(_flowId, "webhook", "order-1", _staleRunId, candidate, Timeout);
        })).ToArray();

        // Act
        gate.SetResult();
        var owners = await Task.WhenAll(attempts);

        // Assert
        var winner = await _sut.FindRunIdByIdempotencyKeyAsync(_flowId, "webhook", "order-1");
        Assert.NotNull(winner);
        Assert.Contains(winner.Value, candidates);
        Assert.Single(candidates, c => owners[Array.IndexOf(candidates, c)] == c);
        Assert.All(owners, owner => Assert.Equal(winner, owner));
    }

    /// <summary>A clock that only moves when the test says so.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        /// <summary>Moves the clock forward.</summary>
        public void Advance(TimeSpan by) => _now += by;

        /// <inheritdoc/>
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
