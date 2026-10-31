using System.Net;
using System.Text.Json;
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
        _server.FlowRunStore.SearchRunsAsync(Arg.Is<RunSearchQuery>(q => q.Take == 10 && q.IncludeTotal), Arg.Any<CancellationToken>())
            .Returns(new RunSearchPage([run], false, 1));

        // Act
        var response = await _client.GetAsync("/flows/api/runs?includeTotal=true&take=10");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());
        Assert.False(doc.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("search").ValueKind);
    }

    [Fact]
    public async Task GET_api_runs_with_status_filter_passes_status_without_text()
    {
        // Arrange

        // Act
        var response = await _client.GetAsync("/flows/api/runs?status=Failed");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _server.FlowRunStore.Received(1).SearchRunsAsync(
            Arg.Is<RunSearchQuery>(q => q.Status == "Failed" && q.Text == null && q.RunId == null && q.StartedFrom == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GET_api_runs_text_search_does_not_search_step_payloads_by_default()
    {
        // Arrange — step payload search is opt-in; neither a bare search nor the old deep=false
        // may reach FlowSteps.

        // Act
        var bare = await _client.GetAsync("/flows/api/runs?search=abc&take=20");
        var quick = await _client.GetAsync("/flows/api/runs?search=abc&take=20&deep=false");

        // Assert
        Assert.Equal(HttpStatusCode.OK, bare.StatusCode);
        Assert.Equal(HttpStatusCode.OK, quick.StatusCode);
        await _server.FlowRunStore.Received(2).SearchRunsAsync(
            Arg.Is<RunSearchQuery>(q => q.Text == "abc" && !q.IncludeStepPayloads && q.Take == 20),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GET_api_runs_text_search_is_bounded_by_the_default_window()
    {
        // Arrange — exact window arithmetic is covered by RunListRequestTests; here the endpoint
        // must hand the store a lower bound seven days back from the server clock.
        var expected = DateTimeOffset.UtcNow.AddDays(-7);

        // Act
        var response = await _client.GetAsync("/flows/api/runs?search=abc");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _server.FlowRunStore.Received(1).SearchRunsAsync(
            Arg.Is<RunSearchQuery>(q => q.StartedFrom.HasValue
                && q.StartedFrom.Value > expected.AddMinutes(-5)
                && q.StartedFrom.Value < expected.AddMinutes(5)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GET_api_runs_deep_true_opts_in_to_step_payloads_inside_a_window()
    {
        // Arrange

        // Act
        var response = await _client.GetAsync("/flows/api/runs?search=tx-7788&deep=true");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _server.FlowRunStore.Received(1).SearchRunsAsync(
            Arg.Is<RunSearchQuery>(q => q.Text == "tx-7788" && q.IncludeStepPayloads && q.StartedFrom.HasValue),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GET_api_runs_full_guid_is_a_run_id_lookup_without_text_or_window()
    {
        // Arrange
        var runId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync("/flows/api/runs?includeTotal=true&search=" + runId.ToString("D").ToUpperInvariant());

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _server.FlowRunStore.Received(1).SearchRunsAsync(
            Arg.Is<RunSearchQuery>(q => q.RunId == runId && q.Text == null && q.StartedFrom == null && !q.IncludeStepPayloads),
            Arg.Any<CancellationToken>());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("runId", doc.RootElement.GetProperty("search").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task GET_api_runs_resolves_blocked_search_alias_to_skipped_status()
    {
        // Arrange — "Blocked" is the dashboard's display label for the canonical "Skipped"
        // run status; a user searching the label must get the Skipped status filter.

        // Act
        var response = await _client.GetAsync("/flows/api/runs?search=blocked");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _server.FlowRunStore.Received(1).SearchRunsAsync(
            Arg.Is<RunSearchQuery>(q => q.Status == "Skipped" && q.Text == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GET_api_runs_status_synonym_is_applied_as_status_filter_and_reported()
    {
        // Arrange — the production case: "Completed" is not a run status, and used to fall through
        // to a scan of every step payload in retention.
        _server.FlowRunStore.SearchRunsAsync(Arg.Any<RunSearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new RunSearchPage(Array.Empty<FlowRunRecord>(), false, 3971));

        // Act
        var response = await _client.GetAsync("/flows/api/runs?includeTotal=true&search=Completed");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _server.FlowRunStore.Received(1).SearchRunsAsync(
            Arg.Is<RunSearchQuery>(q => q.Status == "Succeeded" && q.Text == null && q.StartedFrom == null && q.IncludeTotal),
            Arg.Any<CancellationToken>());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var search = doc.RootElement.GetProperty("search");
        Assert.Equal("status", search.GetProperty("kind").GetString());
        Assert.Equal("Succeeded", search.GetProperty("appliedStatus").GetString());
        Assert.Equal("Completed", search.GetProperty("alias").GetString());
        Assert.Equal(3971, doc.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GET_api_runs_explicit_status_filter_wins_over_a_status_named_term()
    {
        // Arrange

        // Act
        var response = await _client.GetAsync("/flows/api/runs?status=Failed&search=Succeeded");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _server.FlowRunStore.Received(1).SearchRunsAsync(
            Arg.Is<RunSearchQuery>(q => q.Status == "Failed" && q.Text == "Succeeded"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GET_api_runs_text_search_reports_hasMore_and_a_null_total()
    {
        // Arrange
        var runs = new[]
        {
            new FlowRunRecord { Id = Guid.NewGuid(), Status = "Failed" },
            new FlowRunRecord { Id = Guid.NewGuid(), Status = "Failed" },
        };
        _server.FlowRunStore.SearchRunsAsync(Arg.Any<RunSearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new RunSearchPage(runs, true, null));

        // Act
        var response = await _client.GetAsync("/flows/api/runs?includeTotal=true&take=2&search=order-");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("total").ValueKind);
        Assert.True(doc.RootElement.GetProperty("hasMore").GetBoolean());
        var search = doc.RootElement.GetProperty("search");
        Assert.Equal("text", search.GetProperty("kind").GetString());
        Assert.True(search.GetProperty("windowDefaulted").GetBoolean());
        Assert.False(search.GetProperty("stepPayloads").GetBoolean());
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
        _server.OutputsRepository.GetTriggerHeadersAsync(id).Returns(default(IReadOnlyDictionary<string, string>?)); // codeql[cs/useless-upcast] disambiguates NSubstitute overload

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
        _server.FlowRunStore.GetRunDetailAsync(Arg.Any<Guid>()).Returns(default(FlowRunRecord?));

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
        _server.FlowRunStore.GetRunDetailAsync(Arg.Any<Guid>()).Returns(default(FlowRunRecord?));

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
        _server.FlowRunStore.GetRunDetailAsync(Arg.Any<Guid>()).Returns(default(FlowRunRecord?));

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
        _server.FlowRunStore.GetRunDetailAsync(Arg.Any<Guid>()).Returns(default(FlowRunRecord?));

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
            ctx!.Flow.Id == flowId && ctx.Trigger.Key == "webhook"), Arg.Any<CancellationToken>());
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
