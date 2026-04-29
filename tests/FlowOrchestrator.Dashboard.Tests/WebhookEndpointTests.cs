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
        // Arrange
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id);
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });

        // Act
        var response = await _client.PostAsync($"/flows/api/webhook/{id}", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _server.FlowOrchestrator.Received(1).TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task POST_webhook_by_slug_returns_200()
    {
        // Arrange
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id, slug: "order-received");
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });

        // Act
        var response = await _client.PostAsync("/flows/api/webhook/order-received", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task POST_webhook_returns_404_when_flow_not_found()
    {
        // Arrange
        _server.FlowRepository.GetAllFlowsAsync().Returns(Array.Empty<IFlowDefinition>());

        // Act
        var response = await _client.PostAsync($"/flows/api/webhook/{Guid.NewGuid()}", null);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task POST_webhook_returns_403_when_flow_disabled()
    {
        // Arrange
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id);
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = false });

        // Act
        var response = await _client.PostAsync($"/flows/api/webhook/{id}", null);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_webhook_with_correct_secret_header_returns_200()
    {
        // Arrange
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id, secret: "supersecret");
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });

        var request = new HttpRequestMessage(HttpMethod.Post, $"/flows/api/webhook/{id}");
        request.Headers.Add("X-Webhook-Key", "supersecret");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task POST_webhook_with_bearer_token_secret_returns_200()
    {
        // Arrange
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id, secret: "mytoken");
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });

        var request = new HttpRequestMessage(HttpMethod.Post, $"/flows/api/webhook/{id}");
        request.Headers.Add("Authorization", "Bearer mytoken");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task POST_webhook_with_wrong_secret_returns_401()
    {
        // Arrange
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id, secret: "correctsecret");
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });

        var request = new HttpRequestMessage(HttpMethod.Post, $"/flows/api/webhook/{id}");
        request.Headers.Add("X-Webhook-Key", "wrongsecret");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task POST_webhook_missing_secret_header_returns_401()
    {
        // Arrange
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id, secret: "required");
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });

        // Act
        var response = await _client.PostAsync($"/flows/api/webhook/{id}", null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task POST_webhook_with_invalid_json_returns_400()
    {
        // Arrange
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id);
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });

        var content = new StringContent("not-json", System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync($"/flows/api/webhook/{id}", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
