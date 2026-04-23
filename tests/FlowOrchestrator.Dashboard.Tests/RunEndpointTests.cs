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
        _server.FlowRunStore.GetRunsAsync(null, 0, 50)
            .Returns(Array.Empty<FlowRunRecord>());

        var response = await _client.GetAsync("/flows/api/runs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GET_api_runs_with_includeTotal_returns_paged_response()
    {
        var run = new FlowRunRecord { Id = Guid.NewGuid(), Status = "Succeeded", FlowName = "F" };
        _server.FlowRunStore.GetRunsPageAsync(null, null, 0, 10, null)
            .Returns(([run], 1));

        var response = await _client.GetAsync("/flows/api/runs?includeTotal=true&take=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"total\"");
        body.Should().Contain("\"items\"");
    }

    [Fact]
    public async Task GET_api_runs_with_status_filter_uses_GetRunsPageAsync()
    {
        _server.FlowRunStore.GetRunsPageAsync(null, "Failed", 0, 50, null)
            .Returns((Array.Empty<FlowRunRecord>(), 0));

        var response = await _client.GetAsync("/flows/api/runs?status=Failed");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _server.FlowRunStore.Received(1).GetRunsPageAsync(null, "Failed", 0, 50, null);
    }

    [Fact]
    public async Task GET_api_runs_active_returns_200()
    {
        _server.FlowRunStore.GetActiveRunsAsync().Returns(Array.Empty<FlowRunRecord>());

        var response = await _client.GetAsync("/flows/api/runs/active");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GET_api_runs_stats_returns_200()
    {
        _server.FlowRunStore.GetStatisticsAsync()
            .Returns(new DashboardStatistics { TotalFlows = 3, ActiveRuns = 1 });

        var response = await _client.GetAsync("/flows/api/runs/stats");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("totalFlows");
    }

    [Fact]
    public async Task GET_api_runs_by_id_returns_200_for_existing_run()
    {
        var id = Guid.NewGuid();
        var run = new FlowRunRecord { Id = id, Status = "Succeeded", FlowName = "MyFlow" };
        _server.FlowRunStore.GetRunDetailAsync(id).Returns(run);
        _server.OutputsRepository.GetTriggerHeadersAsync(id).Returns((IReadOnlyDictionary<string, string>?)null);

        var response = await _client.GetAsync($"/flows/api/runs/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("MyFlow");
    }

    [Fact]
    public async Task GET_api_runs_by_id_returns_404_for_missing_run()
    {
        _server.FlowRunStore.GetRunDetailAsync(Arg.Any<Guid>()).Returns((FlowRunRecord?)null);

        var response = await _client.GetAsync($"/flows/api/runs/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GET_api_runs_steps_returns_200_with_steps()
    {
        var id = Guid.NewGuid();
        var run = new FlowRunRecord
        {
            Id = id,
            Status = "Succeeded",
            Steps = [new FlowStepRecord { RunId = id, StepKey = "step1", StepType = "T", Status = "Succeeded" }]
        };
        _server.FlowRunStore.GetRunDetailAsync(id).Returns(run);

        var response = await _client.GetAsync($"/flows/api/runs/{id}/steps");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("step1");
    }

    [Fact]
    public async Task GET_api_runs_steps_returns_404_for_missing_run()
    {
        _server.FlowRunStore.GetRunDetailAsync(Arg.Any<Guid>()).Returns((FlowRunRecord?)null);

        var response = await _client.GetAsync($"/flows/api/runs/{Guid.NewGuid()}/steps");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GET_api_runs_events_returns_200()
    {
        var runId = Guid.NewGuid();
        _server.EventReader.GetRunEventsAsync(runId, 0, 200)
            .Returns([new FlowEventRecord { RunId = runId, Sequence = 1, Type = "step.started" }]);

        var response = await _client.GetAsync($"/flows/api/runs/{runId}/events");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("step.started");
    }

    [Fact]
    public async Task GET_api_runs_control_returns_200()
    {
        var runId = Guid.NewGuid();
        _server.RunControlStore.GetRunControlAsync(runId)
            .Returns(new FlowRunControlRecord { RunId = runId, TriggerKey = "manual" });

        var response = await _client.GetAsync($"/flows/api/runs/{runId}/control");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task POST_runs_cancel_returns_200_and_requests_cancel()
    {
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = Guid.NewGuid(), Status = "Running" });
        _server.RunControlStore.RequestCancelAsync(runId, Arg.Any<string?>()).Returns(true);

        var response = await _client.PostAsync($"/flows/api/runs/{runId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _server.RunControlStore.Received(1).RequestCancelAsync(runId, Arg.Any<string?>());
    }

    [Fact]
    public async Task POST_retry_returns_404_when_run_not_found()
    {
        _server.FlowRunStore.GetRunDetailAsync(Arg.Any<Guid>()).Returns((FlowRunRecord?)null);

        var response = await _client.PostAsync($"/flows/api/runs/{Guid.NewGuid()}/steps/step1/retry", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task POST_retry_returns_400_when_step_not_failed()
    {
        var runId = Guid.NewGuid();
        var run = new FlowRunRecord
        {
            Id = runId,
            FlowId = Guid.NewGuid(),
            Status = "Running",
            Steps = [new FlowStepRecord { RunId = runId, StepKey = "step1", StepType = "T", Status = "Running" }]
        };
        _server.FlowRunStore.GetRunDetailAsync(runId).Returns(run);

        var response = await _client.PostAsync($"/flows/api/runs/{runId}/steps/step1/retry", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task POST_rerun_returns_404_when_run_missing()
    {
        _server.FlowRunStore.GetRunDetailAsync(Arg.Any<Guid>()).Returns((FlowRunRecord?)null);

        var response = await _client.PostAsync($"/flows/api/runs/{Guid.NewGuid()}/rerun", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task POST_rerun_returns_404_when_flow_missing_from_repository()
    {
        var runId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = flowId, Status = "Failed", TriggerKey = "manual" });
        _server.FlowRepository.GetAllFlowsAsync().Returns(Array.Empty<IFlowDefinition>());

        var response = await _client.PostAsync($"/flows/api/runs/{runId}/rerun", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task POST_rerun_returns_200_and_triggers_new_run()
    {
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

        var response = await _client.PostAsync($"/flows/api/runs/{runId}/rerun", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("runId");
        body.Should().Contain("sourceRunId");
        await _server.FlowTrigger.Received(1).TriggerAsync(Arg.Is<ITriggerContext>(ctx =>
            ctx.Flow.Id == flowId && ctx.Trigger.Key == "webhook"));
    }

    [Fact]
    public async Task POST_retry_returns_200_for_failed_step()
    {
        var runId = Guid.NewGuid();
        var run = new FlowRunRecord
        {
            Id = runId,
            FlowId = Guid.NewGuid(),
            Status = "Failed",
            Steps = [new FlowStepRecord { RunId = runId, StepKey = "step1", StepType = "T", Status = "Failed" }]
        };
        _server.FlowRunStore.GetRunDetailAsync(runId).Returns(run);

        var response = await _client.PostAsync($"/flows/api/runs/{runId}/steps/step1/retry", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("success");
    }
}
