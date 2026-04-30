using System.Net;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

public sealed class RunEndpointTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;

    public RunEndpointTests() => _client = _server.CreateClient();

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task GET_api_runs_returns_200()
    {
        // Arrange
        _server.FlowRunStore.GetRunsAsync(null, 0, 50)
            .Returns(Array.Empty<FlowRunRecord>());

        // Act
        var response = await _client.GetAsync("/flows/api/runs");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GET_api_runs_with_includeTotal_returns_paged_response()
    {
        // Arrange
        var run = new FlowRunRecord { Id = Guid.NewGuid(), Status = "Succeeded", FlowName = "F" };
        _server.FlowRunStore.GetRunsPageAsync(null, null, 0, 10, null)
            .Returns(([run], 1));

        // Act
        var response = await _client.GetAsync("/flows/api/runs?includeTotal=true&take=10");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"total\"", body);
        Assert.Contains("\"items\"", body);
    }

    [Fact]
    public async Task GET_api_runs_with_status_filter_uses_GetRunsPageAsync()
    {
        // Arrange
        _server.FlowRunStore.GetRunsPageAsync(null, "Failed", 0, 50, null)
            .Returns((Array.Empty<FlowRunRecord>(), 0));

        // Act
        var response = await _client.GetAsync("/flows/api/runs?status=Failed");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _server.FlowRunStore.Received(1).GetRunsPageAsync(null, "Failed", 0, 50, null);
    }

    [Fact]
    public async Task GET_api_runs_active_returns_200()
    {
        // Arrange
        _server.FlowRunStore.GetActiveRunsAsync().Returns(Array.Empty<FlowRunRecord>());

        // Act
        var response = await _client.GetAsync("/flows/api/runs/active");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GET_api_runs_stats_returns_200()
    {
        // Arrange
        _server.FlowRunStore.GetStatisticsAsync()
            .Returns(new DashboardStatistics { TotalFlows = 3, ActiveRuns = 1 });

        // Act
        var response = await _client.GetAsync("/flows/api/runs/stats");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("totalFlows", body);
    }

    [Fact]
    public async Task GET_api_runs_by_id_returns_200_for_existing_run()
    {
        // Arrange
        var id = Guid.NewGuid();
        var run = new FlowRunRecord { Id = id, Status = "Succeeded", FlowName = "MyFlow" };
        _server.FlowRunStore.GetRunDetailAsync(id).Returns(run);
        _server.OutputsRepository.GetTriggerHeadersAsync(id).Returns((IReadOnlyDictionary<string, string>?)null);

        // Act
        var response = await _client.GetAsync($"/flows/api/runs/{id}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("MyFlow", body);
    }

    [Fact]
    public async Task GET_api_runs_by_id_returns_404_for_missing_run()
    {
        // Arrange
        _server.FlowRunStore.GetRunDetailAsync(Arg.Any<Guid>()).Returns((FlowRunRecord?)null);

        // Act
        var response = await _client.GetAsync($"/flows/api/runs/{Guid.NewGuid()}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GET_api_runs_steps_returns_200_with_steps()
    {
        // Arrange
        var id = Guid.NewGuid();
        var run = new FlowRunRecord
        {
            Id = id,
            Status = "Succeeded",
            Steps = [new FlowStepRecord { RunId = id, StepKey = "step1", StepType = "T", Status = "Succeeded" }]
        };
        _server.FlowRunStore.GetRunDetailAsync(id).Returns(run);

        // Act
        var response = await _client.GetAsync($"/flows/api/runs/{id}/steps");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("step1", body);
    }

    [Fact]
    public async Task GET_api_runs_steps_returns_404_for_missing_run()
    {
        // Arrange
        _server.FlowRunStore.GetRunDetailAsync(Arg.Any<Guid>()).Returns((FlowRunRecord?)null);

        // Act
        var response = await _client.GetAsync($"/flows/api/runs/{Guid.NewGuid()}/steps");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GET_api_runs_events_returns_200()
    {
        // Arrange
        var runId = Guid.NewGuid();
        _server.EventReader.GetRunEventsAsync(runId, 0, 200)
            .Returns([new FlowEventRecord { RunId = runId, Sequence = 1, Type = "step.started" }]);

        // Act
        var response = await _client.GetAsync($"/flows/api/runs/{runId}/events");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("step.started", body);
    }

    [Fact]
    public async Task GET_api_runs_control_returns_200()
    {
        // Arrange
        var runId = Guid.NewGuid();
        _server.RunControlStore.GetRunControlAsync(runId)
            .Returns(new FlowRunControlRecord { RunId = runId, TriggerKey = "manual" });

        // Act
        var response = await _client.GetAsync($"/flows/api/runs/{runId}/control");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task POST_runs_cancel_returns_200_and_requests_cancel()
    {
        // Arrange
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = Guid.NewGuid(), Status = "Running" });
        _server.RunControlStore.RequestCancelAsync(runId, Arg.Any<string?>()).Returns(true);

        // Act
        var response = await _client.PostAsync($"/flows/api/runs/{runId}/cancel", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _server.RunControlStore.Received(1).RequestCancelAsync(runId, Arg.Any<string?>());
    }

    [Fact]
    public async Task POST_retry_returns_404_when_run_not_found()
    {
        // Arrange
        _server.FlowRunStore.GetRunDetailAsync(Arg.Any<Guid>()).Returns((FlowRunRecord?)null);

        // Act
        var response = await _client.PostAsync($"/flows/api/runs/{Guid.NewGuid()}/steps/step1/retry", null);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task POST_retry_returns_400_when_step_not_failed()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var run = new FlowRunRecord
        {
            Id = runId,
            FlowId = Guid.NewGuid(),
            Status = "Running",
            Steps = [new FlowStepRecord { RunId = runId, StepKey = "step1", StepType = "T", Status = "Running" }]
        };
        _server.FlowRunStore.GetRunDetailAsync(runId).Returns(run);

        // Act
        var response = await _client.PostAsync($"/flows/api/runs/{runId}/steps/step1/retry", null);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task POST_rerun_returns_404_when_run_missing()
    {
        // Arrange
        _server.FlowRunStore.GetRunDetailAsync(Arg.Any<Guid>()).Returns((FlowRunRecord?)null);

        // Act
        var response = await _client.PostAsync($"/flows/api/runs/{Guid.NewGuid()}/rerun", null);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task POST_rerun_returns_404_when_flow_missing_from_repository()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = flowId, Status = "Failed", TriggerKey = "manual" });
        _server.FlowRepository.GetAllFlowsAsync().Returns(Array.Empty<IFlowDefinition>());

        // Act
        var response = await _client.PostAsync($"/flows/api/runs/{runId}/rerun", null);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task POST_rerun_returns_200_and_triggers_new_run()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowRunStore.GetRunDetailAsync(runId).Returns(new FlowRunRecord
        {
            Id = runId,
            FlowId = flowId,
            Status = "Failed",
            TriggerKey = "webhook",
            TriggerDataJson = "{\"orderId\":42}"
        });

        // Act
        var response = await _client.PostAsync($"/flows/api/runs/{runId}/rerun", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("runId", body);
        Assert.Contains("sourceRunId", body);
        await _server.FlowOrchestrator.Received(1).TriggerAsync(Arg.Is<ITriggerContext>(ctx =>
            ctx.Flow.Id == flowId && ctx.Trigger.Key == "webhook"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task POST_retry_returns_200_for_failed_step()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var run = new FlowRunRecord
        {
            Id = runId,
            FlowId = Guid.NewGuid(),
            Status = "Failed",
            Steps = [new FlowStepRecord { RunId = runId, StepKey = "step1", StepType = "T", Status = "Failed" }]
        };
        _server.FlowRunStore.GetRunDetailAsync(runId).Returns(run);

        // Act
        var response = await _client.PostAsync($"/flows/api/runs/{runId}/steps/step1/retry", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("success", body);
    }
}
