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
}
