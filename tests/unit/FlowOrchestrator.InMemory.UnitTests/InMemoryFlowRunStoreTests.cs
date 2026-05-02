using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.InMemory.Tests;

public class InMemoryFlowRunStoreTests
{
    private readonly InMemoryFlowRunStore _sut = new();

    [Fact]
    public async Task StartRunAsync_CreatesRunningRecord()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        // Act
        var record = await _sut.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);

        // Assert
        Assert.Equal(runId, record.Id);
        Assert.Equal(flowId, record.FlowId);
        Assert.Equal("Running", record.Status);
    }

    [Fact]
    public async Task RecordStepStartAsync_CreatesStepRecord()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "TestFlow", runId, "manual", null, null);

        // Act
        await _sut.RecordStepStartAsync(runId, "step1", "LogMessage", "{}", "job1");

        // Assert
        var detail = await _sut.GetRunDetailAsync(runId);
        Assert.NotNull(detail);
        Assert.Single(detail!.Steps!);
        Assert.Equal("step1", detail.Steps![0].StepKey);
        Assert.Equal("Running", detail.Steps[0].Status);
        Assert.Equal(1, detail.Steps[0].AttemptCount);
        Assert.Single(detail.Steps[0].Attempts!);
        Assert.Equal(1, detail.Steps[0].Attempts![0].Attempt);
    }

    [Fact]
    public async Task RecordStepCompleteAsync_UpdatesStepStatus()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "TestFlow", runId, "manual", null, null);
        await _sut.RecordStepStartAsync(runId, "step1", "LogMessage", null, null);

        // Act
        await _sut.RecordStepCompleteAsync(runId, "step1", "Succeeded", "{\"result\":1}", null);

        // Assert
        var detail = await _sut.GetRunDetailAsync(runId);
        Assert.Equal("Succeeded", detail!.Steps![0].Status);
        Assert.Equal("{\"result\":1}", detail.Steps[0].OutputJson);
        Assert.NotNull(detail.Steps[0].CompletedAt);
        Assert.Equal(1, detail.Steps[0].AttemptCount);
        Assert.Single(detail.Steps[0].Attempts!);
        Assert.Equal("Succeeded", detail.Steps[0].Attempts![0].Status);
    }

    [Fact]
    public async Task RecordStepStartAsync_MultipleStarts_CreatesAttemptHistory()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "TestFlow", runId, "manual", null, null);

        // Act
        await _sut.RecordStepStartAsync(runId, "step1", "CallExternalApi", "{\"attempt\":1}", "job-1");
        await _sut.RecordStepCompleteAsync(runId, "step1", "Pending", "{\"status\":\"processing\"}", null);
        await _sut.RecordStepStartAsync(runId, "step1", "CallExternalApi", "{\"attempt\":2}", "job-2");
        await _sut.RecordStepCompleteAsync(runId, "step1", "Succeeded", "{\"status\":\"done\"}", null);

        // Assert
        var detail = await _sut.GetRunDetailAsync(runId);
        Assert.Single(detail!.Steps!);
        Assert.Equal("Succeeded", detail.Steps![0].Status);
        Assert.Equal(2, detail.Steps[0].AttemptCount);
        Assert.Equal(2, detail.Steps[0].Attempts!.Count);
        var attempts = detail.Steps[0].Attempts!;
        Assert.Equal(1, attempts[0].Attempt);
        Assert.Equal("Pending", attempts[0].Status);
        Assert.Equal(2, attempts[1].Attempt);
        Assert.Equal("Succeeded", attempts[1].Status);
    }

    [Fact]
    public async Task CompleteRunAsync_UpdatesRunStatus()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "TestFlow", runId, "manual", null, null);

        // Act
        await _sut.CompleteRunAsync(runId, "Succeeded");

        // Assert
        var detail = await _sut.GetRunDetailAsync(runId);
        Assert.Equal("Succeeded", detail!.Status);
        Assert.NotNull(detail.CompletedAt);
    }

    [Fact]
    public async Task GetRunsAsync_ReturnsAllRuns()
    {
        // Arrange
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow1", Guid.NewGuid(), "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow2", Guid.NewGuid(), "manual", null, null);

        // Act
        var runs = await _sut.GetRunsAsync();

        // Assert
        Assert.Equal(2, runs.Count);
    }

    [Fact]
    public async Task GetRunsAsync_FilterByFlowId()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        await _sut.StartRunAsync(flowId, "Flow1", Guid.NewGuid(), "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow2", Guid.NewGuid(), "manual", null, null);

        // Act
        var runs = await _sut.GetRunsAsync(flowId: flowId);

        // Assert
        Assert.Single(runs);
        Assert.Equal(flowId, runs[0].FlowId);
    }

    [Fact]
    public async Task GetRunsAsync_SkipAndTake()
    {
        // Arrange
        for (int i = 0; i < 5; i++)
            await _sut.StartRunAsync(Guid.NewGuid(), $"Flow{i}", Guid.NewGuid(), "manual", null, null);

        // Act
        var runs = await _sut.GetRunsAsync(skip: 2, take: 2);

        // Assert
        Assert.Equal(2, runs.Count);
    }

    [Fact]
    public async Task GetRunsPageAsync_FiltersByStatus_AndReturnsTotal()
    {
        // Arrange
        var runId1 = Guid.NewGuid();
        var runId2 = Guid.NewGuid();
        var runId3 = Guid.NewGuid();

        await _sut.StartRunAsync(Guid.NewGuid(), "Flow1", runId1, "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow2", runId2, "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow3", runId3, "manual", null, null);
        await _sut.CompleteRunAsync(runId1, "Succeeded");
        await _sut.CompleteRunAsync(runId2, "Succeeded");

        // Act
        var page = await _sut.GetRunsPageAsync(status: "Succeeded", skip: 0, take: 1);

        // Assert
        Assert.Equal(2, page.TotalCount);
        Assert.Single(page.Runs);
        Assert.Equal("Succeeded", page.Runs[0].Status);
    }

    [Fact]
    public async Task GetRunsPageAsync_SearchesByRunFields()
    {
        // Arrange
        var targetRunId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "OrderPipeline", targetRunId, "manual-order", null, "bg-job-1001");
        await _sut.StartRunAsync(Guid.NewGuid(), "EmailPipeline", Guid.NewGuid(), "manual-email", null, "bg-job-1002");

        // Act
        var page = await _sut.GetRunsPageAsync(search: "job-1001");

        // Assert
        Assert.Equal(1, page.TotalCount);
        Assert.Single(page.Runs);
        Assert.Equal(targetRunId, page.Runs[0].Id);
    }

    [Fact]
    public async Task GetRunsPageAsync_SearchesByStepKey()
    {
        // Arrange
        var runId1 = Guid.NewGuid();
        var runId2 = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow1", runId1, "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow2", runId2, "manual", null, null);
        await _sut.RecordStepStartAsync(runId1, "validateOrder", "ValidateOrder", null, null);
        await _sut.RecordStepStartAsync(runId2, "sendEmail", "SendEmail", null, null);

        // Act
        var page = await _sut.GetRunsPageAsync(search: "validate");

        // Assert
        Assert.Equal(1, page.TotalCount);
        Assert.Single(page.Runs);
        Assert.Equal(runId1, page.Runs[0].Id);
    }

    [Fact]
    public async Task GetRunsPageAsync_SearchesByStepErrorMessage()
    {
        // Arrange
        var runId1 = Guid.NewGuid();
        var runId2 = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow1", runId1, "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow2", runId2, "manual", null, null);
        await _sut.RecordStepStartAsync(runId1, "payment", "Payment", null, null);
        await _sut.RecordStepStartAsync(runId2, "notify", "Notify", null, null);
        await _sut.RecordStepCompleteAsync(runId1, "payment", "Failed", null, "Payment timeout on gateway");
        await _sut.RecordStepCompleteAsync(runId2, "notify", "Failed", null, "Template rendering failed");

        // Act
        var page = await _sut.GetRunsPageAsync(search: "timeout");

        // Assert
        Assert.Equal(1, page.TotalCount);
        Assert.Single(page.Runs);
        Assert.Equal(runId1, page.Runs[0].Id);
    }

    [Fact]
    public async Task GetRunsPageAsync_SearchesByAttemptHistoryErrorMessage()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow1", runId, "manual", null, null);
        await _sut.RecordStepStartAsync(runId, "payment", "Payment", null, null);
        await _sut.RecordStepCompleteAsync(runId, "payment", "Pending", null, "Gateway timeout on first attempt");
        await _sut.RecordStepStartAsync(runId, "payment", "Payment", null, null);
        await _sut.RecordStepCompleteAsync(runId, "payment", "Succeeded", "{\"ok\":true}", null);

        // Act
        var page = await _sut.GetRunsPageAsync(search: "timeout on first");

        // Assert
        Assert.Equal(1, page.TotalCount);
        Assert.Single(page.Runs);
        Assert.Equal(runId, page.Runs[0].Id);
    }

    [Fact]
    public async Task GetRunsPageAsync_SearchesByStepOutputJson()
    {
        // Arrange
        var runId1 = Guid.NewGuid();
        var runId2 = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow1", runId1, "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow2", runId2, "manual", null, null);
        await _sut.RecordStepStartAsync(runId1, "payment", "Payment", null, null);
        await _sut.RecordStepStartAsync(runId2, "notify", "Notify", null, null);
        await _sut.RecordStepCompleteAsync(runId1, "payment", "Succeeded", "{\"transactionId\":\"tx-7788\"}", null);
        await _sut.RecordStepCompleteAsync(runId2, "notify", "Succeeded", "{\"message\":\"ok\"}", null);

        // Act
        var page = await _sut.GetRunsPageAsync(search: "tx-7788");

        // Assert
        Assert.Equal(1, page.TotalCount);
        Assert.Single(page.Runs);
        Assert.Equal(runId1, page.Runs[0].Id);
    }

    [Fact]
    public async Task GetRunsPageAsync_CombinesFlowStatusSearchAndPagination()
    {
        // Arrange
        var targetFlowId = Guid.NewGuid();
        var runId1 = Guid.NewGuid();
        var runId2 = Guid.NewGuid();
        var runId3 = Guid.NewGuid();
        await _sut.StartRunAsync(targetFlowId, "FlowA", runId1, "manual", null, null);
        await _sut.StartRunAsync(targetFlowId, "FlowA", runId2, "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "FlowB", runId3, "manual", null, null);
        await _sut.CompleteRunAsync(runId1, "Succeeded");
        await _sut.CompleteRunAsync(runId2, "Failed");
        await _sut.CompleteRunAsync(runId3, "Failed");
        await _sut.RecordStepStartAsync(runId2, "process", "Process", null, null);
        await _sut.RecordStepStartAsync(runId3, "process", "Process", null, null);
        await _sut.RecordStepCompleteAsync(runId2, "process", "Failed", null, "fatal error on flow A");
        await _sut.RecordStepCompleteAsync(runId3, "process", "Failed", null, "fatal error on flow B");

        // Act
        var page = await _sut.GetRunsPageAsync(flowId: targetFlowId, status: "Failed", skip: 0, take: 1, search: "fatal");

        // Assert
        Assert.Equal(1, page.TotalCount);
        Assert.Single(page.Runs);
        Assert.Equal(runId2, page.Runs[0].Id);
    }

    [Fact]
    public async Task GetRunDetailAsync_NonExistentRun_ReturnsNull()
    {
        // Arrange

        // Act
        var result = await _sut.GetRunDetailAsync(Guid.NewGuid());

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetStatisticsAsync_ReturnsCorrectCounts()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var runId1 = Guid.NewGuid();
        var runId2 = Guid.NewGuid();
        await _sut.StartRunAsync(flowId, "Flow", runId1, "manual", null, null);
        await _sut.StartRunAsync(flowId, "Flow", runId2, "manual", null, null);
        await _sut.CompleteRunAsync(runId1, "Succeeded");

        // Act
        var stats = await _sut.GetStatisticsAsync();

        // Assert
        Assert.Equal(1, stats.ActiveRuns);
        Assert.Equal(1, stats.CompletedToday);
    }

    [Fact]
    public async Task GetActiveRunsAsync_ReturnsOnlyRunning()
    {
        // Arrange
        var runId1 = Guid.NewGuid();
        var runId2 = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow", runId1, "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow", runId2, "manual", null, null);
        await _sut.CompleteRunAsync(runId1, "Succeeded");

        // Act
        var active = await _sut.GetActiveRunsAsync();

        // Assert
        Assert.Single(active);
        Assert.Equal(runId2, active[0].Id);
    }

    [Fact]
    public async Task GetRunTimeseriesAsync_HourlyBuckets_CountsByStatusAndComputesPercentiles()
    {
        // Arrange
        var until = DateTimeOffset.UtcNow;
        var since = until - TimeSpan.FromHours(3);
        var flowId = Guid.NewGuid();

        // Two succeeded runs in bucket 0 (3h ago) with durations 100ms / 300ms.
        // One failed run in bucket 1 (2h ago) with duration 500ms.
        // One running run in bucket 2 (1h ago) — no completion.
        await SeedRun(flowId, since.AddMinutes(5), "Succeeded", durationMs: 100);
        await SeedRun(flowId, since.AddMinutes(10), "Succeeded", durationMs: 300);
        await SeedRun(flowId, since.AddHours(1).AddMinutes(20), "Failed", durationMs: 500);
        await SeedRun(flowId, since.AddHours(2).AddMinutes(15), "Running", durationMs: null);

        // Act
        var buckets = await _sut.GetRunTimeseriesAsync(RunTimeseriesGranularity.Hour, since, until);

        // Assert
        Assert.True(buckets.Count >= 3, $"Expected at least 3 buckets, got {buckets.Count}");
        Assert.Equal(2, buckets[0].Total);
        Assert.Equal(2, buckets[0].Succeeded);
        Assert.Equal(0, buckets[0].Failed);
        Assert.Equal(200, buckets[0].P50DurationMs);
        Assert.Equal(290, buckets[0].P95DurationMs);

        Assert.Equal(1, buckets[1].Total);
        Assert.Equal(1, buckets[1].Failed);
        Assert.Equal(500, buckets[1].P50DurationMs);

        Assert.Equal(1, buckets[2].Total);
        Assert.Equal(1, buckets[2].Running);
        Assert.Null(buckets[2].P50DurationMs);
    }

    [Fact]
    public async Task GetRunTimeseriesAsync_FlowIdFilter_ExcludesOtherFlows()
    {
        // Arrange
        var until = DateTimeOffset.UtcNow;
        var since = until - TimeSpan.FromHours(2);
        var flowA = Guid.NewGuid();
        var flowB = Guid.NewGuid();
        await SeedRun(flowA, since.AddMinutes(15), "Succeeded", 100);
        await SeedRun(flowB, since.AddMinutes(20), "Succeeded", 200);
        await SeedRun(flowA, since.AddHours(1).AddMinutes(5), "Failed", 300);

        // Act
        var seriesA = await _sut.GetRunTimeseriesAsync(RunTimeseriesGranularity.Hour, since, until, flowId: flowA);
        var seriesB = await _sut.GetRunTimeseriesAsync(RunTimeseriesGranularity.Hour, since, until, flowId: flowB);

        // Assert
        Assert.Equal(2, seriesA.Sum(b => b.Total));
        Assert.Equal(1, seriesB.Sum(b => b.Total));
    }

    [Fact]
    public async Task GetRunTimeseriesAsync_EmptyWindow_ReturnsZeroFilledBuckets()
    {
        // Arrange — no runs seeded for this window.
        var until = DateTimeOffset.UtcNow;
        var since = until - TimeSpan.FromHours(4);

        // Act
        var buckets = await _sut.GetRunTimeseriesAsync(RunTimeseriesGranularity.Hour, since, until);

        // Assert — buckets are returned even when empty so the timeline has no gaps.
        Assert.True(buckets.Count >= 4);
        Assert.All(buckets, b => Assert.Equal(0, b.Total));
        Assert.All(buckets, b => Assert.Null(b.P50DurationMs));
    }

    [Fact]
    public async Task GetRunTimeseriesAsync_DayGranularity_30DayWindow_AggregatesByDay()
    {
        // Arrange
        var until = DateTimeOffset.UtcNow;
        var since = until - TimeSpan.FromDays(30);
        var flowId = Guid.NewGuid();
        await SeedRun(flowId, until - TimeSpan.FromDays(15), "Succeeded", 100);
        await SeedRun(flowId, until - TimeSpan.FromDays(15) - TimeSpan.FromHours(2), "Failed", 200);
        await SeedRun(flowId, until - TimeSpan.FromDays(2), "Succeeded", 150);

        // Act
        var buckets = await _sut.GetRunTimeseriesAsync(RunTimeseriesGranularity.Day, since, until);

        // Assert
        Assert.True(buckets.Count >= 30);
        Assert.Equal(3, buckets.Sum(b => b.Total));
        Assert.Equal(2, buckets.Sum(b => b.Succeeded));
        Assert.Equal(1, buckets.Sum(b => b.Failed));
    }

    private async Task SeedRun(Guid flowId, DateTimeOffset startedAt, string status, double? durationMs)
    {
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(flowId, "Flow", runId, "manual", null, null);
        var record = (await _sut.GetRunDetailAsync(runId))!;
        record.StartedAt = startedAt;
        if (status != "Running" && durationMs.HasValue)
        {
            record.CompletedAt = startedAt + TimeSpan.FromMilliseconds(durationMs.Value);
            record.Status = status;
        }
    }
}
