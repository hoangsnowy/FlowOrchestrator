using System.Net;
using System.Net.Http.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

public sealed class WebhookEndpointTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;

    public WebhookEndpointTests() => _client = _server.CreateClient();

    public void Dispose() => _server.Dispose();

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IFlowDefinition MakeWebhookFlow(Guid id, string? secret = null, string? slug = null)
    {
        var inputs = new Dictionary<string, object?>();
        if (secret is not null) inputs["webhookSecret"] = secret;
        if (slug is not null) inputs["webhookSlug"] = slug;

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(id);
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["webhook"] = new TriggerMetadata { Type = TriggerType.Webhook, Inputs = inputs }
            }
        });
        return flow;
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task POST_webhook_by_guid_returns_200()
    {
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id);
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });

        var response = await _client.PostAsync($"/flows/api/webhook/{id}", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _server.FlowOrchestrator.Received(1).TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task POST_webhook_by_slug_returns_200()
    {
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id, slug: "order-received");
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });

        var response = await _client.PostAsync("/flows/api/webhook/order-received", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task POST_webhook_returns_404_when_flow_not_found()
    {
        _server.FlowRepository.GetAllFlowsAsync().Returns(Array.Empty<IFlowDefinition>());

        var response = await _client.PostAsync($"/flows/api/webhook/{Guid.NewGuid()}", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task POST_webhook_returns_403_when_flow_disabled()
    {
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id);
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = false });

        var response = await _client.PostAsync($"/flows/api/webhook/{id}", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task POST_webhook_with_correct_secret_header_returns_200()
    {
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id, secret: "supersecret");
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });

        var request = new HttpRequestMessage(HttpMethod.Post, $"/flows/api/webhook/{id}");
        request.Headers.Add("X-Webhook-Key", "supersecret");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task POST_webhook_with_bearer_token_secret_returns_200()
    {
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id, secret: "mytoken");
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });

        var request = new HttpRequestMessage(HttpMethod.Post, $"/flows/api/webhook/{id}");
        request.Headers.Add("Authorization", "Bearer mytoken");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task POST_webhook_with_wrong_secret_returns_401()
    {
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id, secret: "correctsecret");
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });

        var request = new HttpRequestMessage(HttpMethod.Post, $"/flows/api/webhook/{id}");
        request.Headers.Add("X-Webhook-Key", "wrongsecret");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task POST_webhook_missing_secret_header_returns_401()
    {
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id, secret: "required");
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });

        var response = await _client.PostAsync($"/flows/api/webhook/{id}", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task POST_webhook_with_invalid_json_returns_400()
    {
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id);
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });

        var content = new StringContent("not-json", System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PostAsync($"/flows/api/webhook/{id}", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
