using FlowOrchestrator.InMemory;
using FluentAssertions;

namespace FlowOrchestrator.InMemory.Tests;

public class InMemoryFlowRunStoreTests
{
    private readonly InMemoryFlowRunStore _sut = new();

    [Fact]
    public async Task StartRunAsync_CreatesRunningRecord()
    {
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var record = await _sut.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);

        record.Id.Should().Be(runId);
        record.FlowId.Should().Be(flowId);
        record.Status.Should().Be("Running");
    }

    [Fact]
    public async Task RecordStepStartAsync_CreatesStepRecord()
    {
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "TestFlow", runId, "manual", null, null);

        await _sut.RecordStepStartAsync(runId, "step1", "LogMessage", "{}", "job1");

        var detail = await _sut.GetRunDetailAsync(runId);
        detail!.Steps.Should().HaveCount(1);
        detail.Steps![0].StepKey.Should().Be("step1");
        detail.Steps[0].Status.Should().Be("Running");
        detail.Steps[0].AttemptCount.Should().Be(1);
        detail.Steps[0].Attempts.Should().HaveCount(1);
        detail.Steps[0].Attempts![0].Attempt.Should().Be(1);
    }

    [Fact]
    public async Task RecordStepCompleteAsync_UpdatesStepStatus()
    {
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "TestFlow", runId, "manual", null, null);
        await _sut.RecordStepStartAsync(runId, "step1", "LogMessage", null, null);

        await _sut.RecordStepCompleteAsync(runId, "step1", "Succeeded", "{\"result\":1}", null);

        var detail = await _sut.GetRunDetailAsync(runId);
        detail!.Steps![0].Status.Should().Be("Succeeded");
        detail.Steps[0].OutputJson.Should().Be("{\"result\":1}");
        detail.Steps[0].CompletedAt.Should().NotBeNull();
        detail.Steps[0].AttemptCount.Should().Be(1);
        detail.Steps[0].Attempts.Should().HaveCount(1);
        detail.Steps[0].Attempts![0].Status.Should().Be("Succeeded");
    }

    [Fact]
    public async Task RecordStepStartAsync_MultipleStarts_CreatesAttemptHistory()
    {
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "TestFlow", runId, "manual", null, null);

        await _sut.RecordStepStartAsync(runId, "step1", "CallExternalApi", "{\"attempt\":1}", "job-1");
        await _sut.RecordStepCompleteAsync(runId, "step1", "Pending", "{\"status\":\"processing\"}", null);
        await _sut.RecordStepStartAsync(runId, "step1", "CallExternalApi", "{\"attempt\":2}", "job-2");
        await _sut.RecordStepCompleteAsync(runId, "step1", "Succeeded", "{\"status\":\"done\"}", null);

        var detail = await _sut.GetRunDetailAsync(runId);
        detail!.Steps.Should().ContainSingle();
        detail.Steps![0].Status.Should().Be("Succeeded");
        detail.Steps[0].AttemptCount.Should().Be(2);
        detail.Steps[0].Attempts.Should().HaveCount(2);
        var attempts = detail.Steps[0].Attempts!;
        attempts[0].Attempt.Should().Be(1);
        attempts[0].Status.Should().Be("Pending");
        attempts[1].Attempt.Should().Be(2);
        attempts[1].Status.Should().Be("Succeeded");
    }

    [Fact]
    public async Task CompleteRunAsync_UpdatesRunStatus()
    {
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "TestFlow", runId, "manual", null, null);

        await _sut.CompleteRunAsync(runId, "Succeeded");

        var detail = await _sut.GetRunDetailAsync(runId);
        detail!.Status.Should().Be("Succeeded");
        detail.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task GetRunsAsync_ReturnsAllRuns()
    {
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow1", Guid.NewGuid(), "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow2", Guid.NewGuid(), "manual", null, null);

        var runs = await _sut.GetRunsAsync();

        runs.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetRunsAsync_FilterByFlowId()
    {
        var flowId = Guid.NewGuid();
        await _sut.StartRunAsync(flowId, "Flow1", Guid.NewGuid(), "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow2", Guid.NewGuid(), "manual", null, null);

        var runs = await _sut.GetRunsAsync(flowId: flowId);

        runs.Should().HaveCount(1);
        runs[0].FlowId.Should().Be(flowId);
    }

    [Fact]
    public async Task GetRunsAsync_SkipAndTake()
    {
        for (int i = 0; i < 5; i++)
            await _sut.StartRunAsync(Guid.NewGuid(), $"Flow{i}", Guid.NewGuid(), "manual", null, null);

        var runs = await _sut.GetRunsAsync(skip: 2, take: 2);

        runs.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetRunsPageAsync_FiltersByStatus_AndReturnsTotal()
    {
        var runId1 = Guid.NewGuid();
        var runId2 = Guid.NewGuid();
        var runId3 = Guid.NewGuid();

        await _sut.StartRunAsync(Guid.NewGuid(), "Flow1", runId1, "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow2", runId2, "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow3", runId3, "manual", null, null);
        await _sut.CompleteRunAsync(runId1, "Succeeded");
        await _sut.CompleteRunAsync(runId2, "Succeeded");

        var page = await _sut.GetRunsPageAsync(status: "Succeeded", skip: 0, take: 1);

        page.TotalCount.Should().Be(2);
        page.Runs.Should().HaveCount(1);
        page.Runs[0].Status.Should().Be("Succeeded");
    }

    [Fact]
    public async Task GetRunsPageAsync_SearchesByRunFields()
    {
        var targetRunId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "OrderPipeline", targetRunId, "manual-order", null, "bg-job-1001");
        await _sut.StartRunAsync(Guid.NewGuid(), "EmailPipeline", Guid.NewGuid(), "manual-email", null, "bg-job-1002");

        var page = await _sut.GetRunsPageAsync(search: "job-1001");

        page.TotalCount.Should().Be(1);
        page.Runs.Should().ContainSingle();
        page.Runs[0].Id.Should().Be(targetRunId);
    }

    [Fact]
    public async Task GetRunsPageAsync_SearchesByStepKey()
    {
        var runId1 = Guid.NewGuid();
        var runId2 = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow1", runId1, "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow2", runId2, "manual", null, null);
        await _sut.RecordStepStartAsync(runId1, "validateOrder", "ValidateOrder", null, null);
        await _sut.RecordStepStartAsync(runId2, "sendEmail", "SendEmail", null, null);

        var page = await _sut.GetRunsPageAsync(search: "validate");

        page.TotalCount.Should().Be(1);
        page.Runs.Should().ContainSingle();
        page.Runs[0].Id.Should().Be(runId1);
    }

    [Fact]
    public async Task GetRunsPageAsync_SearchesByStepErrorMessage()
    {
        var runId1 = Guid.NewGuid();
        var runId2 = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow1", runId1, "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow2", runId2, "manual", null, null);
        await _sut.RecordStepStartAsync(runId1, "payment", "Payment", null, null);
        await _sut.RecordStepStartAsync(runId2, "notify", "Notify", null, null);
        await _sut.RecordStepCompleteAsync(runId1, "payment", "Failed", null, "Payment timeout on gateway");
        await _sut.RecordStepCompleteAsync(runId2, "notify", "Failed", null, "Template rendering failed");

        var page = await _sut.GetRunsPageAsync(search: "timeout");

        page.TotalCount.Should().Be(1);
        page.Runs.Should().ContainSingle();
        page.Runs[0].Id.Should().Be(runId1);
    }

    [Fact]
    public async Task GetRunsPageAsync_SearchesByAttemptHistoryErrorMessage()
    {
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow1", runId, "manual", null, null);
        await _sut.RecordStepStartAsync(runId, "payment", "Payment", null, null);
        await _sut.RecordStepCompleteAsync(runId, "payment", "Pending", null, "Gateway timeout on first attempt");
        await _sut.RecordStepStartAsync(runId, "payment", "Payment", null, null);
        await _sut.RecordStepCompleteAsync(runId, "payment", "Succeeded", "{\"ok\":true}", null);

        var page = await _sut.GetRunsPageAsync(search: "timeout on first");

        page.TotalCount.Should().Be(1);
        page.Runs.Should().ContainSingle();
        page.Runs[0].Id.Should().Be(runId);
    }

    [Fact]
    public async Task GetRunsPageAsync_SearchesByStepOutputJson()
    {
        var runId1 = Guid.NewGuid();
        var runId2 = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow1", runId1, "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow2", runId2, "manual", null, null);
        await _sut.RecordStepStartAsync(runId1, "payment", "Payment", null, null);
        await _sut.RecordStepStartAsync(runId2, "notify", "Notify", null, null);
        await _sut.RecordStepCompleteAsync(runId1, "payment", "Succeeded", "{\"transactionId\":\"tx-7788\"}", null);
        await _sut.RecordStepCompleteAsync(runId2, "notify", "Succeeded", "{\"message\":\"ok\"}", null);

        var page = await _sut.GetRunsPageAsync(search: "tx-7788");

        page.TotalCount.Should().Be(1);
        page.Runs.Should().ContainSingle();
        page.Runs[0].Id.Should().Be(runId1);
    }

    [Fact]
    public async Task GetRunsPageAsync_CombinesFlowStatusSearchAndPagination()
    {
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

        var page = await _sut.GetRunsPageAsync(flowId: targetFlowId, status: "Failed", skip: 0, take: 1, search: "fatal");

        page.TotalCount.Should().Be(1);
        page.Runs.Should().ContainSingle();
        page.Runs[0].Id.Should().Be(runId2);
    }

    [Fact]
    public async Task GetRunDetailAsync_NonExistentRun_ReturnsNull()
    {
        var result = await _sut.GetRunDetailAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetStatisticsAsync_ReturnsCorrectCounts()
    {
        var flowId = Guid.NewGuid();
        var runId1 = Guid.NewGuid();
        var runId2 = Guid.NewGuid();
        await _sut.StartRunAsync(flowId, "Flow", runId1, "manual", null, null);
        await _sut.StartRunAsync(flowId, "Flow", runId2, "manual", null, null);
        await _sut.CompleteRunAsync(runId1, "Succeeded");

        var stats = await _sut.GetStatisticsAsync();

        stats.ActiveRuns.Should().Be(1);
        stats.CompletedToday.Should().Be(1);
    }

    [Fact]
    public async Task GetActiveRunsAsync_ReturnsOnlyRunning()
    {
        var runId1 = Guid.NewGuid();
        var runId2 = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow", runId1, "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow", runId2, "manual", null, null);
        await _sut.CompleteRunAsync(runId1, "Succeeded");

        var active = await _sut.GetActiveRunsAsync();

        active.Should().HaveCount(1);
        active[0].Id.Should().Be(runId2);
    }
}
