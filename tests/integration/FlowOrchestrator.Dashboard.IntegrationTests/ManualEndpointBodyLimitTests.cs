using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Integration coverage for the request-body size cap on the dashboard control-plane endpoints
/// (manual trigger, run cancel, cron PUT). Before the fix these endpoints read the request body
/// with no cap — unlike the webhook path — so a reachable caller could stream an arbitrarily large
/// payload and exhaust server memory. Each endpoint must now reject an oversized body with HTTP 413
/// while still accepting a normal small payload.
/// </summary>
public sealed class ManualEndpointBodyLimitTests : IDisposable
{
    // Small cap so tests don't have to allocate megabytes. Reused for every endpoint via
    // FlowDashboardOptions.WebhookSecurity.MaxBodyBytes (the single ingress knob the fix reuses).
    private const long CapBytes = 1024;

    private readonly DashboardTestServer _server;
    private readonly HttpClient _client;

    public ManualEndpointBodyLimitTests()
    {
        _server = new DashboardTestServer(options => options.WebhookSecurity.MaxBodyBytes = CapBytes);
        _client = _server.CreateClient();
    }

    public void Dispose() => _server.Dispose();

    private static string JobId(Guid flowId, string triggerKey) => $"flow-{flowId:D}-{triggerKey}";

    private static StringContent OversizedJson()
    {
        // Valid JSON, but well over the configured cap.
        var big = new string('x', (int)CapBytes * 4);
        return new StringContent($$"""{"blob":"{{big}}"}""", Encoding.UTF8, "application/json");
    }

    // ── Manual trigger: POST /api/flows/{id}/trigger ─────────────────────────

    [Fact]
    public async Task POST_trigger_rejects_oversized_body_with_413()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest());
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        using var content = OversizedJson();

        // Act
        using var response = await _client.PostAsync($"/flows/api/flows/{flowId}/trigger", content);

        // Assert — payload too large; the engine is never invoked.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await _server.FlowOrchestrator.DidNotReceive()
            .TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task POST_trigger_accepts_normal_body()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest());
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        using var content = new StringContent("""{"orderId":42}""", Encoding.UTF8, "application/json");

        // Act
        using var response = await _client.PostAsync($"/flows/api/flows/{flowId}/trigger", content);

        // Assert — normal small payload is unaffected by the cap.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _server.FlowOrchestrator.Received(1)
            .TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task POST_trigger_rejects_oversized_chunked_body_with_413()
    {
        // Arrange — chunked transfer (no Content-Length) exercises the streaming cap, not the
        // early Content-Length short-circuit.
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest());
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });

        var big = new string('y', (int)CapBytes * 4);
        using var content = new StringContent($$"""{"blob":"{{big}}"}""", Encoding.UTF8, "application/json");
        content.Headers.ContentLength = null; // force chunked
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/flows/api/flows/{flowId}/trigger")
        {
            Content = content,
        };
        request.Headers.TransferEncodingChunked = true;

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await _server.FlowOrchestrator.DidNotReceive()
            .TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    // ── Run cancel: POST /api/runs/{runId}/cancel ────────────────────────────

    [Fact]
    public async Task POST_cancel_rejects_oversized_body_with_413()
    {
        // Arrange
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = Guid.NewGuid(), Status = "Running" });
        using var content = OversizedJson();

        // Act
        using var response = await _client.PostAsync($"/flows/api/runs/{runId}/cancel", content);

        // Assert — rejected before any cancel is requested.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await _server.RunControlStore.DidNotReceive().RequestCancelAsync(runId, Arg.Any<string?>());
    }

    [Fact]
    public async Task POST_cancel_accepts_normal_reason_body()
    {
        // Arrange
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = Guid.NewGuid(), Status = "Running" });
        _server.RunControlStore.RequestCancelAsync(runId, Arg.Any<string?>()).Returns(true);
        using var content = new StringContent("""{"reason":"operator stop"}""", Encoding.UTF8, "application/json");

        // Act
        using var response = await _client.PostAsync($"/flows/api/runs/{runId}/cancel", content);

        // Assert — normal reason body still flows through to the control store.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _server.RunControlStore.Received(1).RequestCancelAsync(runId, "operator stop");
    }

    // ── Cron update: PUT /api/schedules/{jobId}/cron ─────────────────────────

    [Fact]
    public async Task PUT_cron_rejects_oversized_body_with_413()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var jobId = JobId(flowId, "cron");
        using var content = OversizedJson();

        // Act
        using var response = await _client.PutAsync($"/flows/api/schedules/{jobId}/cron", content);

        // Assert — rejected before the dispatcher is touched.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        _server.TriggerDispatcher.DidNotReceive()
            .RegisterOrUpdate(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task PUT_cron_accepts_normal_body()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var jobId = JobId(flowId, "cron");
        const string newCron = "0 0 * * *";
        var manifestJson = """{"triggers":{"cron":{"inputs":{"cronExpression":"0 * * * *"}}}}""";
        _server.FlowStore.GetByIdAsync(flowId)
            .Returns(new FlowDefinitionRecord
            {
                Id = flowId,
                Name = "NightlyFlow",
                IsEnabled = true,
                ManifestJson = manifestJson,
            });
        using var content = new StringContent(
            JsonSerializer.Serialize(new { cronExpression = newCron }),
            Encoding.UTF8,
            "application/json");

        // Act
        using var response = await _client.PutAsync($"/flows/api/schedules/{jobId}/cron", content);

        // Assert — normal small payload still updates the cron.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _server.TriggerDispatcher.Received(1).RegisterOrUpdate(jobId, flowId, "cron", newCron);
    }
}
