using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer.Tests;

public sealed class SqlFlowRunStoreTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlFlowRunStore _store;
    private readonly string _connectionString;

    public SqlFlowRunStoreTests(SqlServerFixture fixture)
    {
        _store = new SqlFlowRunStore(fixture.ConnectionString);
        _connectionString = fixture.ConnectionString;
    }

    [Fact]
    public async Task StartRunAsync_creates_run_with_Running_status()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        // Act
        var run = await _store.StartRunAsync(flowId, "MyFlow", runId, "manual", null, "job-1");

        // Assert
        Assert.Equal(runId, run.Id);
        Assert.Equal(flowId, run.FlowId);
        Assert.Equal("MyFlow", run.FlowName);
        Assert.Equal("Running", run.Status);
        Assert.Equal("manual", run.TriggerKey);
        Assert.Equal("job-1", run.BackgroundJobId);
        Assert.NotEqual(default, run.StartedAt);
        Assert.Null(run.CompletedAt);
    }

    [Fact]
    public async Task Full_run_state_machine()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(flowId, "SM_Flow", runId, "webhook", """{"x":1}""", null);

        // Act
        await _store.RecordStepStartAsync(runId, "step1", "LogMessage", """{"msg":"hi"}""", "j1");
        await _store.RecordStepCompleteAsync(runId, "step1", "Succeeded", """{"ok":true}""", null);
        await _store.CompleteRunAsync(runId, "Succeeded");

        // Assert
        var detail = await _store.GetRunDetailAsync(runId);
        Assert.NotNull(detail);
        Assert.Equal("Succeeded", detail!.Status);
        Assert.NotNull(detail.CompletedAt);
        Assert.Single(detail.Steps!);
        Assert.Equal("step1", detail.Steps![0].StepKey);
        Assert.Equal("Succeeded", detail.Steps![0].Status);
        Assert.Single(detail.Steps![0].Attempts!);
    }

    [Fact]
    public async Task RecordStepStartAsync_increments_attempt_counter()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(Guid.NewGuid(), "AttemptFlow", runId, "manual", null, null);

        // Act
        await _store.RecordStepStartAsync(runId, "s1", "T", null, null);
        await _store.RecordStepCompleteAsync(runId, "s1", "Failed", null, "err");
        await _store.RecordStepStartAsync(runId, "s1", "T", null, null);
        await _store.RecordStepCompleteAsync(runId, "s1", "Succeeded", null, null);

        // Assert
        var detail = await _store.GetRunDetailAsync(runId);
        Assert.Equal(2, detail!.Steps![0].Attempts!.Count);
        Assert.Equal(1, detail!.Steps![0].Attempts![0].Attempt);
        Assert.Equal(2, detail!.Steps![0].Attempts![1].Attempt);
    }

    [Fact]
    public async Task GetRunDetailAsync_returns_null_for_unknown_run()
    {
        // Arrange

        // Act
        var result = await _store.GetRunDetailAsync(Guid.NewGuid());

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetRunsPageAsync_respects_skip_and_take()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            await _store.StartRunAsync(flowId, "PageFlow", Guid.NewGuid(), "manual", null, null);

        // Act
        var (page1, total) = await _store.GetRunsPageAsync(flowId, null, 0, 2, null);
        var (page2, _) = await _store.GetRunsPageAsync(flowId, null, 2, 2, null);

        // Assert
        Assert.True(total >= 5);
        Assert.Equal(2, page1.Count);
        Assert.Equal(2, page2.Count);
    }

    [Fact]
    public async Task GetRunsPageAsync_filters_by_status()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(flowId, "StatusFilter", runId, "manual", null, null);
        await _store.CompleteRunAsync(runId, "Succeeded");

        // Act
        var (runs, total) = await _store.GetRunsPageAsync(flowId, "Succeeded", 0, 10, null);

        // Assert
        Assert.True(total >= 1);
        Assert.True(runs.All(r => r.Status == "Succeeded"));
    }

    [Fact]
    public async Task GetStatisticsAsync_returns_counts()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(Guid.NewGuid(), "StatFlow", runId, "manual", null, null);

        // Act
        var stats = await _store.GetStatisticsAsync();

        // Assert
        Assert.True(stats.TotalFlows >= 1);
        Assert.True(stats.ActiveRuns >= 1);
    }

    [Fact]
    public async Task GetStatisticsAsync_CompletedToday_buckets_in_UTC_and_excludes_older_runs()
    {
        // Arrange
        var baseline = (await _store.GetStatisticsAsync()).CompletedToday;
        var todayRun = Guid.NewGuid();
        await _store.StartRunAsync(Guid.NewGuid(), "StatToday", todayRun, "manual", null, null);
        await _store.CompleteRunAsync(todayRun, "Succeeded"); // CompletedAt = SYSDATETIMEOFFSET() (today, UTC)
        var oldRun = Guid.NewGuid();
        await _store.StartRunAsync(Guid.NewGuid(), "StatOld", oldRun, "manual", null, null);
        await _store.CompleteRunAsync(oldRun, "Succeeded");
        await using (var conn = new SqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "UPDATE FlowRuns SET CompletedAt = DATEADD(day, -2, SYSUTCDATETIME()) WHERE Id = @id", conn);
            cmd.Parameters.AddWithValue("@id", oldRun);
            await cmd.ExecuteNonQueryAsync();
        }

        // Act
        var stats = await _store.GetStatisticsAsync();

        // Assert
        Assert.Equal(baseline + 1, stats.CompletedToday); // today's run counted; the 2-day-old run excluded
    }

    [Fact]
    public async Task RetryStepAsync_resets_step_and_run_to_Running()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(flowId, "RetryFlow", runId, "manual", null, null);
        await _store.RecordStepStartAsync(runId, "step1", "T", null, null);
        await _store.RecordStepCompleteAsync(runId, "step1", "Failed", null, "oops");
        await _store.CompleteRunAsync(runId, "Failed");

        // Act
        await _store.RetryStepAsync(runId, "step1");

        // Assert
        var detail = await _store.GetRunDetailAsync(runId);
        Assert.Equal("Running", detail!.Status);
        Assert.Equal("Running", detail.Steps![0].Status);
    }

    [Fact]
    public async Task GetActiveRunsAsync_returns_running_runs()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(Guid.NewGuid(), "ActiveFlow", runId, "manual", null, null);

        // Act
        var active = await _store.GetActiveRunsAsync();

        // Assert
        Assert.Contains(active, r => r.Id == runId);
    }

    [Fact]
    public async Task GetRunsPageAsync_search_matches_flow_name()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var uniqueName = $"SearchableFlow_{Guid.NewGuid():N}";
        await _store.StartRunAsync(flowId, uniqueName, Guid.NewGuid(), "manual", null, null);

        // Act
        var (runs, total) = await _store.GetRunsPageAsync(null, null, 0, 10, uniqueName);

        // Assert
        Assert.True(total >= 1);
        Assert.True(runs.All(r => r.FlowName == uniqueName));
    }

    [Fact]
    public async Task GetRunsPageAsync_search_matches_current_step_output_json()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var token = $"txid{Guid.NewGuid():N}";
        await _store.StartRunAsync(flowId, "OutputJsonFlow", runId, "manual", null, null);
        await _store.RecordStepStartAsync(runId, "pay", "Pay", null, null);
        await _store.RecordStepCompleteAsync(runId, "pay", "Succeeded", $"{{\"transactionId\":\"{token}\"}}", null);

        // Act — search inside the current step's output JSON is preserved.
        var (runs, total) = await _store.GetRunsPageAsync(null, null, 0, 10, token);

        // Assert (also locks the single-pass COUNT(*) OVER() total to the exact match count)
        Assert.Equal(1, total);
        Assert.Single(runs);
        Assert.Equal(runId, runs[0].Id);
    }

    [Fact]
    public async Task GetRunsPageAsync_search_excludes_superseded_attempt_history()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var token = $"attempttok{Guid.NewGuid():N}";
        await _store.StartRunAsync(flowId, "AttemptHistoryFlow", runId, "manual", null, null);
        await _store.RecordStepStartAsync(runId, "pay", "Pay", null, null);
        await _store.RecordStepCompleteAsync(runId, "pay", "Failed", null, $"gateway {token} failed");
        await _store.RecordStepStartAsync(runId, "pay", "Pay", null, null);
        await _store.RecordStepCompleteAsync(runId, "pay", "Succeeded", "{\"ok\":true}", null);

        // Act — the token survives only in the superseded attempt history, which is no longer searched.
        var (runs, total) = await _store.GetRunsPageAsync(null, null, 0, 10, token);

        // Assert
        Assert.Equal(0, total);
        Assert.Empty(runs);
    }

    [Fact]
    public async Task GetRunsPageAsync_filters_by_started_date_range()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        await _store.StartRunAsync(flowId, "DateRangeFlow", Guid.NewGuid(), "manual", null, null);
        var now = DateTimeOffset.UtcNow;

        // Act
        var (inRange, inTotal) = await _store.GetRunsPageAsync(flowId, null, 0, 10, null, deepSearch: true, startedFrom: now.AddHours(-1), startedTo: now.AddHours(1));
        var (afterRange, afterTotal) = await _store.GetRunsPageAsync(flowId, null, 0, 10, null, deepSearch: true, startedFrom: now.AddHours(1));

        // Assert
        Assert.Equal(1, inTotal);
        Assert.Single(inRange);
        Assert.Equal(0, afterTotal);
        Assert.Empty(afterRange);
    }

    [Fact]
    public async Task GetRunsPageAsync_empty_page_past_end_still_reports_full_total()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
            await _store.StartRunAsync(flowId, "PastEndFlow", Guid.NewGuid(), "manual", null, null);

        // Act — skip past the last matching row: COUNT(*) OVER() emits no row, so the store must
        // fall back to an explicit COUNT rather than report total 0.
        var (runs, total) = await _store.GetRunsPageAsync(flowId, null, 100, 10, null);

        // Assert
        Assert.Empty(runs);
        Assert.Equal(3, total);
    }

    [Fact]
    public async Task ExtendDeadlineAsync_NoControlRecord_ReturnsFalse()
    {
        // Arrange — no ConfigureRunAsync, so no control record exists.
        var runId = Guid.NewGuid();

        // Act
        var result = await _store.ExtendDeadlineAsync(runId, DateTimeOffset.UtcNow.AddMinutes(10));

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ExtendDeadlineAsync_AfterTimeout_ClearsTimeoutLatchAndRefreshesDeadline()
    {
        // Arrange — a run latched TimedOut (also sets the cancel fields).
        var runId = Guid.NewGuid();
        await _store.ConfigureRunAsync(runId, Guid.NewGuid(), "manual", null, DateTimeOffset.UtcNow.AddMinutes(-5));
        await _store.MarkTimedOutAsync(runId, "deadline exceeded");
        var newDeadline = DateTimeOffset.UtcNow.AddMinutes(10);

        // Act
        var result = await _store.ExtendDeadlineAsync(runId, newDeadline);

        // Assert — the timeout-induced termination is fully un-latched and the deadline refreshed.
        Assert.True(result);
        var control = await _store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.Null(control!.TimedOutAtUtc);
        Assert.False(control.CancelRequested);
        Assert.Null(control.CancelReason);
        Assert.Null(control.CancelRequestedAtUtc);
        Assert.NotNull(control.TimeoutAtUtc);
    }

    [Fact]
    public async Task ExtendDeadlineAsync_PreservesGenuineUserCancellation()
    {
        // Arrange — a user-cancelled run (TimedOutAtUtc stays null).
        var runId = Guid.NewGuid();
        await _store.ConfigureRunAsync(runId, Guid.NewGuid(), "manual", null, null);
        await _store.RequestCancelAsync(runId, "user requested");

        // Act
        var result = await _store.ExtendDeadlineAsync(runId, DateTimeOffset.UtcNow.AddMinutes(10));

        // Assert — only timeout-induced state is cleared; the user cancellation survives.
        Assert.True(result);
        var control = await _store.GetRunControlAsync(runId);
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
        await _store.ConfigureRunAsync(runId, Guid.NewGuid(), "manual", null, DateTimeOffset.UtcNow.AddMinutes(5));

        // Act
        var result = await _store.ExtendDeadlineAsync(runId, null);

        // Assert
        Assert.True(result);
        var control = await _store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.Null(control!.TimeoutAtUtc);
    }
}
