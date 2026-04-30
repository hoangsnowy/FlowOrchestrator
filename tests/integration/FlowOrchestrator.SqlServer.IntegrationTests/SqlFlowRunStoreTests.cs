using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.SqlServer.Tests;

public sealed class SqlFlowRunStoreTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlFlowRunStore _store;

    public SqlFlowRunStoreTests(SqlServerFixture fixture)
    {
        _store = new SqlFlowRunStore(fixture.ConnectionString);
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
}
