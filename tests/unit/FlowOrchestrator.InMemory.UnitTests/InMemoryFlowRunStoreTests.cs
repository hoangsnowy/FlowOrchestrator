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
    public async Task GetRunsPageAsync_DoesNotSearchSupersededAttemptHistory()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow1", runId, "manual", null, null);
        await _sut.RecordStepStartAsync(runId, "payment", "Payment", null, null);
        await _sut.RecordStepCompleteAsync(runId, "payment", "Pending", null, "Gateway timeout on first attempt");
        await _sut.RecordStepStartAsync(runId, "payment", "Payment", null, null);
        await _sut.RecordStepCompleteAsync(runId, "payment", "Succeeded", "{\"ok\":true}", null);

        // Act — "timeout on first attempt" now lives only in the superseded attempt
        // history (the current step row was overwritten by the successful retry).
        // Attempt history is intentionally excluded from search.
        var page = await _sut.GetRunsPageAsync(search: "timeout on first");

        // Assert
        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Runs);
    }

    [Fact]
    public async Task GetRunsPageAsync_FiltersByStartedDateRange()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(flowId, "Flow1", runId, "manual", null, null);
        var now = DateTimeOffset.UtcNow;

        // Act
        var inRange = await _sut.GetRunsPageAsync(flowId, null, 0, 50, null, deepSearch: true, startedFrom: now.AddHours(-1), startedTo: now.AddHours(1));
        var afterRange = await _sut.GetRunsPageAsync(flowId, null, 0, 50, null, deepSearch: true, startedFrom: now.AddHours(1));

        // Assert
        Assert.Equal(1, inRange.TotalCount);
        Assert.Single(inRange.Runs);
        Assert.Equal(0, afterRange.TotalCount);
        Assert.Empty(afterRange.Runs);
    }

    [Fact]
    public async Task GetRunDetailAsync_SkippedStep_CarriesSkipReasonInErrorMessage()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow1", runId, "manual", null, null);
        await _sut.RecordStepStartAsync(runId, "notify", "Notify", null, null);
        await _sut.RecordStepCompleteAsync(runId, "notify", "Skipped", null, "Recipient opted out");

        // Act — a handler-returned Skipped result's FailedReason is persisted as the
        // step's ErrorMessage and surfaced in step detail (what the dashboard renders).
        var detail = await _sut.GetRunDetailAsync(runId);

        // Assert
        var step = Assert.Single(detail!.Steps!);
        Assert.Equal("Skipped", step.Status);
        Assert.Equal("Recipient opted out", step.ErrorMessage);
    }

    [Fact]
    public async Task GetRunsPageAsync_QuickSearch_DoesNotMatchStepOutputOrError()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "FlowQ", runId, "manual", null, null);
        await _sut.RecordStepStartAsync(runId, "pay", "Pay", null, null);
        await _sut.RecordStepCompleteAsync(runId, "pay", "Failed", "{\"tx\":\"deeponly7788\"}", "boom deepmsg9001");

        // Act — quick search (deepSearch:false) scans only top-level run columns.
        var byOutput = await _sut.GetRunsPageAsync(null, null, 0, 50, "deeponly7788", false);
        var byError = await _sut.GetRunsPageAsync(null, null, 0, 50, "deepmsg9001", false);

        // Assert
        Assert.Empty(byOutput.Runs);
        Assert.Empty(byError.Runs);
    }

    [Fact]
    public async Task GetRunsPageAsync_QuickSearch_MatchesTopLevelColumns()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(flowId, "QuickFlowName", runId, "quicktrigger", null, "quickjob1");

        // Act + Assert — every top-level column is matched by quick search.
        foreach (var term in new[] { runId.ToString(), "QuickFlowName", "quicktrigger", "Running", "quickjob1" })
        {
            var page = await _sut.GetRunsPageAsync(null, null, 0, 50, term, false);
            Assert.Contains(page.Runs, r => r.Id == runId);
        }
    }

    [Fact]
    public async Task GetRunsPageAsync_DeepSearch_AndLegacyOverload_MatchStepLevelTerm()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "FlowD", runId, "manual", null, null);
        await _sut.RecordStepStartAsync(runId, "pay", "Pay", null, null);
        await _sut.RecordStepCompleteAsync(runId, "pay", "Succeeded", "{\"tx\":\"steplevel555\"}", null);

        // Act — deep search and the legacy overload both reach into step output.
        var deep = await _sut.GetRunsPageAsync(null, null, 0, 50, "steplevel555", true);
        var legacy = await _sut.GetRunsPageAsync(search: "steplevel555");

        // Assert
        Assert.Contains(deep.Runs, r => r.Id == runId);
        Assert.Contains(legacy.Runs, r => r.Id == runId);
    }

    [Fact]
    public async Task GetRunsPageAsync_LegacyOverload_EqualsDeepSearch()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var rid = Guid.NewGuid();
            ids.Add(rid);
            await _sut.StartRunAsync(flowId, "ParityFlow", rid, "manual", null, null);
            await _sut.RecordStepStartAsync(rid, "s", "T", null, null);
            await _sut.RecordStepCompleteAsync(rid, "s", "Succeeded", "{\"k\":\"parityz\"}", null);
        }

        // Act — the legacy 5-arg overload must equal deepSearch:true for a fixed dataset.
        var legacy = await _sut.GetRunsPageAsync(flowId, null, 0, 50, "parityz");
        var deep = await _sut.GetRunsPageAsync(flowId, null, 0, 50, "parityz", true);

        // Assert
        Assert.Equal(3, legacy.TotalCount);
        Assert.Equal(deep.TotalCount, legacy.TotalCount);
        Assert.Equal(deep.Runs.Count, legacy.Runs.Count);
        foreach (var id in ids)
        {
            Assert.Contains(legacy.Runs, r => r.Id == id);
            Assert.Contains(deep.Runs, r => r.Id == id);
        }
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
        // Arrange — align `since` to an hour boundary so the aggregator's anchor (which
        // floors `since` to the hour) coincides with `since` itself, otherwise bucket 0
        // is the floored hour, not `since`'s hour, and the assertions below shift by one.
        // This was the date-flaky source: when run at e.g. 03:57:03 UTC, `since` was
        // 00:57:03 → anchor floored to 00:00:00 → "since.AddMinutes(5)" landed in bucket 1
        // (the 01:00 hour), not bucket 0. Tests passed only when minute==0.
        var nowUtc = DateTimeOffset.UtcNow.UtcDateTime;
        var nowHourFloor = new DateTimeOffset(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, TimeSpan.Zero);
        var until = nowHourFloor + TimeSpan.FromHours(1);
        var since = until - TimeSpan.FromHours(3);     // since is now exactly on the hour
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

    [Fact]
    public async Task RequestCancelAsync_NoControlRecord_ReturnsFalse()
    {
        // Arrange — no ConfigureRunAsync call, so no control record exists for this run.
        var runId = Guid.NewGuid();

        // Act — must NOT fabricate a phantom record; mirrors the SQL backends' UPDATE-miss.
        var result = await _sut.RequestCancelAsync(runId, "user requested");

        // Assert
        Assert.False(result);
        Assert.Null(await _sut.GetRunControlAsync(runId));
    }

    [Fact]
    public async Task RequestCancelAsync_ExistingControlRecord_ReturnsTrue_AndSetsFlag()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.ConfigureRunAsync(runId, Guid.NewGuid(), "manual", idempotencyKey: null, timeoutAtUtc: null);

        // Act
        var result = await _sut.RequestCancelAsync(runId, "user requested");

        // Assert
        Assert.True(result);
        var control = await _sut.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.True(control!.CancelRequested);
        Assert.Equal("user requested", control.CancelReason);
        Assert.NotNull(control.CancelRequestedAtUtc);
    }

    [Fact]
    public async Task RequestCancelAsync_AlreadyCancelled_ReturnsFalse()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.ConfigureRunAsync(runId, Guid.NewGuid(), "manual", null, null);
        await _sut.RequestCancelAsync(runId, "first");

        // Act
        var second = await _sut.RequestCancelAsync(runId, "second");

        // Assert
        Assert.False(second);
        var control = await _sut.GetRunControlAsync(runId);
        Assert.Equal("first", control!.CancelReason);
    }

    [Fact]
    public async Task MarkTimedOutAsync_NoControlRecord_ReturnsFalse()
    {
        // Arrange — no control record for this run.
        var runId = Guid.NewGuid();

        // Act
        var result = await _sut.MarkTimedOutAsync(runId, "timed out");

        // Assert — must not fabricate a record; parity with the SQL backends.
        Assert.False(result);
        Assert.Null(await _sut.GetRunControlAsync(runId));
    }

    [Fact]
    public async Task MarkTimedOutAsync_ExistingControlRecord_ReturnsTrue_AndSetsFlags()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.ConfigureRunAsync(runId, Guid.NewGuid(), "manual", null, DateTimeOffset.UtcNow.AddMinutes(5));

        // Act
        var result = await _sut.MarkTimedOutAsync(runId, "deadline exceeded");

        // Assert
        Assert.True(result);
        var control = await _sut.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.NotNull(control!.TimedOutAtUtc);
        Assert.True(control.CancelRequested);
        Assert.Equal("deadline exceeded", control.CancelReason);
    }

    [Fact]
    public async Task MarkTimedOutAsync_AlreadyTimedOut_ReturnsFalse()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.ConfigureRunAsync(runId, Guid.NewGuid(), "manual", null, null);
        await _sut.MarkTimedOutAsync(runId, "first timeout");

        // Act
        var second = await _sut.MarkTimedOutAsync(runId, "second timeout");

        // Assert
        Assert.False(second);
    }

    [Fact]
    public async Task ExtendDeadlineAsync_NoControlRecord_ReturnsFalse()
    {
        // Arrange — no ConfigureRunAsync call, so no control record exists for this run.
        var runId = Guid.NewGuid();

        // Act
        var result = await _sut.ExtendDeadlineAsync(runId, DateTimeOffset.UtcNow.AddMinutes(10));

        // Assert
        Assert.False(result);
        Assert.Null(await _sut.GetRunControlAsync(runId));
    }

    [Fact]
    public async Task ExtendDeadlineAsync_AfterTimeout_ClearsTimeoutLatchAndRefreshesDeadline()
    {
        // Arrange — a run that has been latched TimedOut (which also sets the cancel fields).
        var runId = Guid.NewGuid();
        await _sut.ConfigureRunAsync(runId, Guid.NewGuid(), "manual", null, DateTimeOffset.UtcNow.AddMinutes(-5));
        await _sut.MarkTimedOutAsync(runId, "deadline exceeded");
        var newDeadline = DateTimeOffset.UtcNow.AddMinutes(10);

        // Act
        var result = await _sut.ExtendDeadlineAsync(runId, newDeadline);

        // Assert — timeout-induced termination is fully un-latched and the deadline is refreshed.
        Assert.True(result);
        var control = await _sut.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.Equal(newDeadline, control!.TimeoutAtUtc);
        Assert.Null(control.TimedOutAtUtc);
        Assert.False(control.CancelRequested);
        Assert.Null(control.CancelReason);
        Assert.Null(control.CancelRequestedAtUtc);
    }

    [Fact]
    public async Task ExtendDeadlineAsync_PreservesGenuineUserCancellation()
    {
        // Arrange — a run cancelled by the user (TimedOutAtUtc stays null).
        var runId = Guid.NewGuid();
        await _sut.ConfigureRunAsync(runId, Guid.NewGuid(), "manual", null, null);
        await _sut.RequestCancelAsync(runId, "user requested");

        // Act
        var result = await _sut.ExtendDeadlineAsync(runId, DateTimeOffset.UtcNow.AddMinutes(10));

        // Assert — the user cancellation survives; only timeout-induced state is cleared.
        Assert.True(result);
        var control = await _sut.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.True(control!.CancelRequested);
        Assert.Equal("user requested", control.CancelReason);
        Assert.Null(control.TimedOutAtUtc);
    }

    [Fact]
    public async Task ExtendDeadlineAsync_NullDeadline_ClearsTimeoutBound()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.ConfigureRunAsync(runId, Guid.NewGuid(), "manual", null, DateTimeOffset.UtcNow.AddMinutes(5));

        // Act
        var result = await _sut.ExtendDeadlineAsync(runId, null);

        // Assert
        Assert.True(result);
        var control = await _sut.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.Null(control!.TimeoutAtUtc);
    }

    [Fact]
    public async Task CompleteRunIfActiveAsync_TransitionsRunningOnce_ThenReturnsFalse()
    {
        // Arrange — a running run; two concurrent completers race to finish it.
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "F", runId, "manual", null, null);

        // Act — first call wins the transition, second finds it already terminal.
        var first = await _sut.CompleteRunIfActiveAsync(runId, "Succeeded");
        var second = await _sut.CompleteRunIfActiveAsync(runId, "TimedOut");

        // Assert — exactly one transition; the first writer's status stands.
        Assert.True(first);
        Assert.False(second);
        Assert.Equal("Succeeded", await _sut.GetRunStatusAsync(runId));
    }

    [Fact]
    public async Task CompleteRunIfActiveAsync_UnknownRun_ReturnsFalse()
    {
        // Arrange

        // Act
        var result = await _sut.CompleteRunIfActiveAsync(Guid.NewGuid(), "Succeeded");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task CleanupAsync_RetainsRunCompletedExactlyAtCutoff()
    {
        // Arrange — strict less-than semantics: a run completed AT the cutoff is retained,
        // a run completed BEFORE the cutoff is removed (parity with both SQL backends).
        var cutoff = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var atCutoffRunId = Guid.NewGuid();
        var beforeCutoffRunId = Guid.NewGuid();

        await _sut.StartRunAsync(Guid.NewGuid(), "Flow", atCutoffRunId, "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow", beforeCutoffRunId, "manual", null, null);
        await _sut.CompleteRunAsync(atCutoffRunId, "Succeeded");
        await _sut.CompleteRunAsync(beforeCutoffRunId, "Succeeded");

        // Force exact completion timestamps relative to the cutoff.
        (await _sut.GetRunDetailAsync(atCutoffRunId))!.CompletedAt = cutoff;
        (await _sut.GetRunDetailAsync(beforeCutoffRunId))!.CompletedAt = cutoff.AddTicks(-1);

        // Act
        await _sut.CleanupAsync(cutoff, CancellationToken.None);

        // Assert
        Assert.NotNull(await _sut.GetRunDetailAsync(atCutoffRunId));
        Assert.Null(await _sut.GetRunDetailAsync(beforeCutoffRunId));
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
