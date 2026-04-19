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
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var run = await _store.StartRunAsync(flowId, "MyFlow", runId, "manual", null, "job-1");

        run.Id.Should().Be(runId);
        run.FlowId.Should().Be(flowId);
        run.FlowName.Should().Be("MyFlow");
        run.Status.Should().Be("Running");
        run.TriggerKey.Should().Be("manual");
        run.BackgroundJobId.Should().Be("job-1");
        run.StartedAt.Should().NotBe(default);
        run.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Full_run_state_machine()
    {
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(flowId, "SM_Flow", runId, "webhook", """{"x":1}""", null);

        await _store.RecordStepStartAsync(runId, "step1", "LogMessage", """{"msg":"hi"}""", "j1");
        await _store.RecordStepCompleteAsync(runId, "step1", "Succeeded", """{"ok":true}""", null);
        await _store.CompleteRunAsync(runId, "Succeeded");

        var detail = await _store.GetRunDetailAsync(runId);
        detail.Should().NotBeNull();
        detail!.Status.Should().Be("Succeeded");
        detail.CompletedAt.Should().NotBeNull();
        detail.Steps.Should().HaveCount(1);
        detail.Steps![0].StepKey.Should().Be("step1");
        detail.Steps![0].Status.Should().Be("Succeeded");
        detail.Steps![0].Attempts.Should().HaveCount(1);
    }

    [Fact]
    public async Task RecordStepStartAsync_increments_attempt_counter()
    {
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(Guid.NewGuid(), "AttemptFlow", runId, "manual", null, null);

        await _store.RecordStepStartAsync(runId, "s1", "T", null, null);
        await _store.RecordStepCompleteAsync(runId, "s1", "Failed", null, "err");
        await _store.RecordStepStartAsync(runId, "s1", "T", null, null);
        await _store.RecordStepCompleteAsync(runId, "s1", "Succeeded", null, null);

        var detail = await _store.GetRunDetailAsync(runId);
        detail!.Steps![0].Attempts.Should().HaveCount(2);
        detail!.Steps![0].Attempts![0].Attempt.Should().Be(1);
        detail!.Steps![0].Attempts![1].Attempt.Should().Be(2);
    }

    [Fact]
    public async Task GetRunDetailAsync_returns_null_for_unknown_run()
    {
        var result = await _store.GetRunDetailAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetRunsPageAsync_respects_skip_and_take()
    {
        var flowId = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            await _store.StartRunAsync(flowId, "PageFlow", Guid.NewGuid(), "manual", null, null);

        var (page1, total) = await _store.GetRunsPageAsync(flowId, null, 0, 2, null);
        var (page2, _) = await _store.GetRunsPageAsync(flowId, null, 2, 2, null);

        total.Should().BeGreaterThanOrEqualTo(5);
        page1.Should().HaveCount(2);
        page2.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetRunsPageAsync_filters_by_status()
    {
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(flowId, "StatusFilter", runId, "manual", null, null);
        await _store.CompleteRunAsync(runId, "Succeeded");

        var (runs, _) = await _store.GetRunsPageAsync(flowId, "Succeeded", 0, 10, null);

        runs.All(r => r.Status == "Succeeded").Should().BeTrue();
    }

    [Fact]
    public async Task GetStatisticsAsync_returns_counts()
    {
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(Guid.NewGuid(), "StatFlow", runId, "manual", null, null);

        var stats = await _store.GetStatisticsAsync();

        stats.TotalFlows.Should().BeGreaterThanOrEqualTo(1);
        stats.ActiveRuns.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task RetryStepAsync_resets_step_and_run_to_Running()
    {
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(flowId, "RetryFlow", runId, "manual", null, null);
        await _store.RecordStepStartAsync(runId, "step1", "T", null, null);
        await _store.RecordStepCompleteAsync(runId, "step1", "Failed", null, "oops");
        await _store.CompleteRunAsync(runId, "Failed");

        await _store.RetryStepAsync(runId, "step1");

        var detail = await _store.GetRunDetailAsync(runId);
        detail!.Status.Should().Be("Running");
        detail.Steps![0].Status.Should().Be("Running");
    }

    [Fact]
    public async Task GetActiveRunsAsync_returns_running_runs()
    {
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(Guid.NewGuid(), "ActiveFlow", runId, "manual", null, null);

        var active = await _store.GetActiveRunsAsync();

        active.Should().Contain(r => r.Id == runId);
    }

    [Fact]
    public async Task GetRunsPageAsync_search_matches_flow_name()
    {
        var flowId = Guid.NewGuid();
        var uniqueName = $"SearchableFlow_{Guid.NewGuid():N}";
        await _store.StartRunAsync(flowId, uniqueName, Guid.NewGuid(), "manual", null, null);

        var (runs, total) = await _store.GetRunsPageAsync(null, null, 0, 10, uniqueName);

        total.Should().BeGreaterThanOrEqualTo(1);
        runs.All(r => r.FlowName == uniqueName).Should().BeTrue();
    }
}
