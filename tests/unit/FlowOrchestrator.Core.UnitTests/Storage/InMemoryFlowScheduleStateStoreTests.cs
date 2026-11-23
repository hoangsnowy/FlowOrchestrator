using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Tests.Storage;

/// <summary>
/// Contract of the single process-local <see cref="InMemoryFlowScheduleStateStore"/> shared by
/// <c>AddFlowOrchestrator</c> and <c>AddFlowDashboard</c> defaults.
/// </summary>
public sealed class InMemoryFlowScheduleStateStoreTests
{
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static FlowScheduleState State(string jobId, bool paused = false, string? cron = null) => new()
    {
        JobId = jobId,
        FlowId = Guid.NewGuid(),
        FlowName = "Flow",
        TriggerKey = "schedule",
        IsPaused = paused,
        CronOverride = cron,
    };

    [Fact]
    public async Task SaveThenGet_ReturnsTheSavedValues_StampedFromTheClock()
    {
        // Arrange
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var store = new InMemoryFlowScheduleStateStore(new FixedTimeProvider(now));
        var saved = State("job-1", paused: true, cron: "*/5 * * * *");

        // Act
        await store.SaveAsync(saved);
        var loaded = await store.GetAsync("job-1");

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(saved.FlowId, loaded.FlowId);
        Assert.Equal("Flow", loaded.FlowName);
        Assert.Equal("schedule", loaded.TriggerKey);
        Assert.True(loaded.IsPaused);
        Assert.Equal("*/5 * * * *", loaded.CronOverride);
        Assert.Equal(now, loaded.UpdatedAtUtc);
    }

    [Fact]
    public async Task MutatingASavedOrLoadedInstance_DoesNotChangeTheStoredState()
    {
        // Arrange
        var store = new InMemoryFlowScheduleStateStore();
        var saved = State("job-1", cron: "0 * * * *");
        await store.SaveAsync(saved);
        var loaded = await store.GetAsync("job-1");

        // Act
        saved.IsPaused = true;
        loaded!.CronOverride = "*/1 * * * *";
        var reloaded = await store.GetAsync("job-1");

        // Assert
        Assert.False(reloaded!.IsPaused);
        Assert.Equal("0 * * * *", reloaded.CronOverride);
    }

    [Fact]
    public async Task GetAll_ReturnsEveryState_OrderedByJobIdOrdinal()
    {
        // Arrange
        var store = new InMemoryFlowScheduleStateStore();
        await store.SaveAsync(State("job-b"));
        await store.SaveAsync(State("job-a"));
        await store.SaveAsync(State("Job-c"));

        // Act
        var all = await store.GetAllAsync();

        // Assert — ordinal: upper-case 'J' sorts before lower-case 'j'.
        Assert.Equal(["Job-c", "job-a", "job-b"], all.Select(s => s.JobId));
    }

    [Fact]
    public async Task Delete_RemovesTheState()
    {
        // Arrange
        var store = new InMemoryFlowScheduleStateStore();
        await store.SaveAsync(State("job-1"));

        // Act
        await store.DeleteAsync("job-1");

        // Assert
        Assert.Null(await store.GetAsync("job-1"));
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task Get_UnknownJob_ReturnsNull()
    {
        // Arrange
        var store = new InMemoryFlowScheduleStateStore();

        // Act
        var loaded = await store.GetAsync("missing");

        // Assert
        Assert.Null(loaded);
    }
}
