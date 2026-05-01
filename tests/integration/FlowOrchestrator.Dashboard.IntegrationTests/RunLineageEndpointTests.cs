using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Integration tests for the lineage endpoint and the SourceRunId wiring on /rerun
/// and the cancel force-close behaviour. Backs the "Re-run of …" / "Re-run as …"
/// dashboard panels and the zombie-run cancel fix.
/// </summary>
public sealed class RunLineageEndpointTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;

    public RunLineageEndpointTests()
    {
        _client = _server.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    [Fact]
    public async Task POST_rerun_sets_SourceRunId_on_TriggerContext()
    {
        // Arrange
        var sourceRunId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowRunStore.GetRunDetailAsync(sourceRunId).Returns(new FlowRunRecord
        {
            Id = sourceRunId,
            FlowId = flowId,
            Status = "Failed",
            TriggerKey = "manual",
            TriggerDataJson = "{\"x\":1}"
        });

        // Act
        var response = await _client.PostAsync($"/flows/api/runs/{sourceRunId}/rerun", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _server.FlowOrchestrator.Received(1).TriggerAsync(
            Arg.Is<ITriggerContext>(ctx => ctx.SourceRunId == sourceRunId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GET_lineage_returns_404_when_run_missing()
    {
        // Arrange
        _server.FlowRunStore.GetRunDetailAsync(Arg.Any<Guid>()).Returns((FlowRunRecord?)null);

        // Act
        var response = await _client.GetAsync($"/flows/api/runs/{Guid.NewGuid()}/lineage");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GET_lineage_returns_source_when_run_has_SourceRunId()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        var derivedId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(derivedId).Returns(new FlowRunRecord
        {
            Id = derivedId,
            FlowId = Guid.NewGuid(),
            Status = "Succeeded",
            SourceRunId = sourceId,
            FlowName = "DerivedFlow"
        });
        _server.FlowRunStore.GetRunDetailAsync(sourceId).Returns(new FlowRunRecord
        {
            Id = sourceId,
            FlowId = Guid.NewGuid(),
            Status = "Failed",
            FlowName = "SourceFlow"
        });
        _server.FlowRunStore.GetDerivedRunsAsync(derivedId).Returns(Array.Empty<FlowRunRecord>());

        // Act
        var response = await _client.GetAsync($"/flows/api/runs/{derivedId}/lineage");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(sourceId, doc.RootElement.GetProperty("source").GetProperty("id").GetGuid());
        Assert.Equal("Failed", doc.RootElement.GetProperty("source").GetProperty("status").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("derived").GetArrayLength());
    }

    [Fact]
    public async Task GET_lineage_returns_derived_runs_for_original()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var rerun1 = Guid.NewGuid();
        var rerun2 = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(originalId).Returns(new FlowRunRecord
        {
            Id = originalId,
            FlowId = Guid.NewGuid(),
            Status = "Failed",
            FlowName = "OrigFlow"
            // No SourceRunId — this is the original
        });
        _server.FlowRunStore.GetDerivedRunsAsync(originalId).Returns(new[]
        {
            new FlowRunRecord { Id = rerun1, FlowId = Guid.NewGuid(), Status = "Succeeded", FlowName = "OrigFlow", SourceRunId = originalId },
            new FlowRunRecord { Id = rerun2, FlowId = Guid.NewGuid(), Status = "Failed",    FlowName = "OrigFlow", SourceRunId = originalId }
        });

        // Act
        var response = await _client.GetAsync($"/flows/api/runs/{originalId}/lineage");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("source").ValueKind);
        var derived = doc.RootElement.GetProperty("derived");
        Assert.Equal(2, derived.GetArrayLength());
        var derivedIds = derived.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(rerun1, derivedIds);
        Assert.Contains(rerun2, derivedIds);
    }

    [Fact]
    public async Task POST_cancel_force_closes_zombie_run_with_no_active_steps()
    {
        // Arrange — Running run, runtime store reports zero active steps (zombie scenario).
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = Guid.NewGuid(), Status = "Running" });
        _server.RunControlStore.RequestCancelAsync(runId, Arg.Any<string?>()).Returns(true);
        _server.RuntimeStore.GetStepStatusesAsync(runId).Returns(new Dictionary<string, StepStatus>());

        // Act
        var response = await _client.PostAsync($"/flows/api/runs/{runId}/cancel", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("closedImmediately").GetBoolean());
        await _server.FlowRunStore.Received(1).CompleteRunAsync(runId, "Cancelled");
    }

    [Fact]
    public async Task POST_cancel_force_closes_even_when_RequestCancel_returns_false()
    {
        // Arrange — old zombie with no FlowRunControls row → RequestCancelAsync returns false,
        // but we should still force-close because the run is Running with no active steps.
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = Guid.NewGuid(), Status = "Running" });
        _server.RunControlStore.RequestCancelAsync(runId, Arg.Any<string?>()).Returns(false);
        _server.RuntimeStore.GetStepStatusesAsync(runId).Returns(new Dictionary<string, StepStatus>());

        // Act
        var response = await _client.PostAsync($"/flows/api/runs/{runId}/cancel", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("closedImmediately").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("accepted").GetBoolean(),
            "accepted should be true when we force-close, regardless of RequestCancel result");
        await _server.FlowRunStore.Received(1).CompleteRunAsync(runId, "Cancelled");
    }

    [Fact]
    public async Task POST_cancel_does_not_force_close_when_active_steps_exist()
    {
        // Arrange — Running run with one Running step — engine workers will pick up the cancel flag.
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = Guid.NewGuid(), Status = "Running" });
        _server.RunControlStore.RequestCancelAsync(runId, Arg.Any<string?>()).Returns(true);
        _server.RuntimeStore.GetStepStatusesAsync(runId).Returns(new Dictionary<string, StepStatus>
        {
            ["step1"] = StepStatus.Running
        });

        // Act
        var response = await _client.PostAsync($"/flows/api/runs/{runId}/cancel", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("closedImmediately").GetBoolean());
        await _server.FlowRunStore.DidNotReceive().CompleteRunAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Fact]
    public async Task POST_cancel_does_not_force_close_already_terminal_run()
    {
        // Arrange — run is already Failed. Cancel should NOT overwrite to "Cancelled".
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = Guid.NewGuid(), Status = "Failed" });
        _server.RunControlStore.RequestCancelAsync(runId, Arg.Any<string?>()).Returns(false);

        // Act
        var response = await _client.PostAsync($"/flows/api/runs/{runId}/cancel", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("closedImmediately").GetBoolean());
        await _server.FlowRunStore.DidNotReceive().CompleteRunAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }
}
