using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// <see cref="InMemoryFlowRunStore.LeaseUnsentDispatchesAsync"/>: which dispatch-ledger rows count as a
/// hand-off that never returned, and that a lease is handed out once per timeout window.
/// </summary>
public sealed class InMemoryFlowRunStoreUnsentDispatchLeaseTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly InMemoryFlowRunStore _sut;
    private readonly Guid _flowId = Guid.NewGuid();
    private readonly Guid _runId = Guid.NewGuid();

    /// <summary>Puts the store on a clock the tests move by hand.</summary>
    public InMemoryFlowRunStoreUnsentDispatchLeaseTests()
    {
        _sut = new InMemoryFlowRunStore { Clock = _clock };
    }

    [Fact]
    public async Task Only_old_unannotated_unclaimed_rows_with_no_status_or_a_Pending_one_on_a_running_run_are_leased()
    {
        // Arrange
        await _sut.StartRunAsync(_flowId, "LeaseFlow", _runId, "manual", null, null);
        foreach (var key in new[] { "unsent", "annotated", "claimed", "done", "polling" })
        {
            await _sut.TryRecordDispatchAsync(_runId, key);
        }

        await _sut.AnnotateDispatchAsync(_runId, "annotated", "job-1");
        await _sut.TryClaimStepAsync(_runId, "claimed");
        await _sut.RecordStepStartAsync(_runId, "done", "Work", null, null);
        await _sut.RecordStepCompleteAsync(_runId, "done", "Succeeded", null, null);
        await _sut.RecordStepStartAsync(_runId, "polling", "Poll", null, null);
        await _sut.RecordStepCompleteAsync(_runId, "polling", "Pending", null, null);

        var closedRun = Guid.NewGuid();
        await _sut.StartRunAsync(_flowId, "LeaseFlow", closedRun, "manual", null, null);
        await _sut.TryRecordDispatchAsync(closedRun, "unsent");
        await _sut.CompleteRunAsync(closedRun, "Cancelled");

        _clock.Advance(TimeSpan.FromSeconds(61));
        await _sut.TryRecordDispatchAsync(_runId, "young");

        // Act
        var leased = await _sut.LeaseUnsentDispatchesAsync(Timeout, 100);

        // Assert
        Assert.Equal(["polling", "unsent"], leased.Select(l => l.StepKey).Order(StringComparer.Ordinal));
        Assert.All(leased, l => Assert.Equal((_runId, _flowId), (l.RunId, l.FlowId)));
    }

    [Fact]
    public async Task A_leased_row_is_not_leased_again_until_another_timeout_has_passed()
    {
        // Arrange
        await _sut.StartRunAsync(_flowId, "LeaseFlow", _runId, "manual", null, null);
        await _sut.TryRecordDispatchAsync(_runId, "unsent");
        _clock.Advance(TimeSpan.FromSeconds(61));

        // Act
        var first = await _sut.LeaseUnsentDispatchesAsync(Timeout, 100);
        var immediately = await _sut.LeaseUnsentDispatchesAsync(Timeout, 100);
        _clock.Advance(TimeSpan.FromSeconds(61));
        var aWindowLater = await _sut.LeaseUnsentDispatchesAsync(Timeout, 100);

        // Assert
        Assert.Single(first);
        Assert.Empty(immediately);
        Assert.Single(aWindowLater);
    }

    [Fact]
    public async Task An_annotation_after_the_lease_takes_the_row_out_for_good()
    {
        // Arrange
        await _sut.StartRunAsync(_flowId, "LeaseFlow", _runId, "manual", null, null);
        await _sut.TryRecordDispatchAsync(_runId, "unsent");
        _clock.Advance(TimeSpan.FromSeconds(61));
        await _sut.LeaseUnsentDispatchesAsync(Timeout, 100);

        // Act
        await _sut.AnnotateDispatchAsync(_runId, "unsent", "redelivered");
        _clock.Advance(TimeSpan.FromMinutes(10));
        var leased = await _sut.LeaseUnsentDispatchesAsync(Timeout, 100);

        // Assert
        Assert.Empty(leased);
        Assert.True(await _sut.IsStepDispatchedAsync(_runId, "unsent"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    public async Task At_most_maxCount_rows_are_leased_per_call(int maxCount, int expected)
    {
        // Arrange
        await _sut.StartRunAsync(_flowId, "LeaseFlow", _runId, "manual", null, null);
        foreach (var key in new[] { "a", "b", "c" })
        {
            await _sut.TryRecordDispatchAsync(_runId, key);
        }

        _clock.Advance(TimeSpan.FromSeconds(61));

        // Act
        var leased = await _sut.LeaseUnsentDispatchesAsync(Timeout, maxCount);

        // Assert
        Assert.Equal(expected, leased.Count);
    }

    [Fact]
    public async Task Concurrent_callers_never_lease_the_same_row_twice()
    {
        // Arrange
        await _sut.StartRunAsync(_flowId, "LeaseFlow", _runId, "manual", null, null);
        for (var i = 0; i < 50; i++)
        {
            await _sut.TryRecordDispatchAsync(_runId, "s" + i);
        }

        _clock.Advance(TimeSpan.FromSeconds(61));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                return await _sut.LeaseUnsentDispatchesAsync(Timeout, 100);
            }))
            .ToArray();

        // Act
        gate.SetResult();
        var results = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        var keys = results.SelectMany(r => r).Select(l => l.StepKey).ToList();
        Assert.Equal(50, keys.Count);
        Assert.Equal(50, keys.Distinct(StringComparer.Ordinal).Count());
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
