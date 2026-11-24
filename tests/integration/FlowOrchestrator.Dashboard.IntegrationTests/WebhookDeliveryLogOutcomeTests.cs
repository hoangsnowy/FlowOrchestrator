using System.Net;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// The webhook delivery log (Webhooks tab) must record the outcome the sender actually received. The legacy
/// <c>webhookSecret</c> / <c>X-Webhook-Key</c> check runs after the security pipeline has let a request
/// through, so a delivery refused there must be logged as rejected with that 401 — never as accepted.
/// </summary>
/// <remarks>Each test owns its server (<c>using var</c>) so NSubstitute arrangements never leak across tests.</remarks>
public sealed class WebhookDeliveryLogOutcomeTests
{
    private const string Slug = "legacy-key";
    private const string SharedSecret = "correct-shared-secret";

    private static Guid RegisterLegacyKeyFlow(DashboardTestServer server)
    {
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["webhook"] = new TriggerMetadata
                {
                    Type = TriggerType.Webhook,
                    Inputs = new Dictionary<string, object?> { ["webhookSlug"] = Slug, ["webhookSecret"] = SharedSecret },
                },
            },
            Steps = new StepCollection { ["s"] = new StepMetadata { Type = "Noop" } },
        });
        server.Flows = new FlowRegistry([flow]);
        server.FlowStore.GetByIdAsync(flowId).Returns(new FlowDefinitionRecord { Id = flowId, IsEnabled = true });
        return flowId;
    }

    private static HttpRequestMessage Delivery(string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/flows/api/webhook/{Slug}")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        if (key is not null)
            request.Headers.Add("X-Webhook-Key", key);
        return request;
    }

    private static async Task<IReadOnlyList<WebhookRejectionRecord>> LogAsync(DashboardTestServer server, Guid flowId)
    {
        var log = server.Services.GetRequiredService<IWebhookRejectionStore>();
        var page = await log.QueryAsync(new WebhookRejectionQuery(flowId, null, null, IncludeAccepted: true, IncludeRejected: true, Skip: 0, Take: 50));
        return page.Items;
    }

    [Theory]
    [InlineData("guess-0001")]
    [InlineData(null)]
    public async Task Delivery_refused_by_the_legacy_key_check_is_logged_as_rejected_with_its_401(string? key)
    {
        // Arrange
        using var server = new DashboardTestServer();
        var flowId = RegisterLegacyKeyFlow(server);
        using var request = Delivery(key);

        // Act
        using var response = await server.Client.SendAsync(request);
        var rows = await LogAsync(server, flowId);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(rows, r => r.IsAccepted);
        var row = Assert.Single(rows);
        Assert.Equal(401, row.StatusCode);
        Assert.Equal("secret_invalid", row.Reason);
        Assert.Equal("webhook", row.TriggerKey);
        await server.FlowOrchestrator.DidNotReceive().TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delivery_with_the_right_legacy_key_is_logged_once_as_accepted()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var flowId = RegisterLegacyKeyFlow(server);
        using var request = Delivery(SharedSecret);

        // Act
        using var response = await server.Client.SendAsync(request);
        var rows = await LogAsync(server, flowId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single(rows);
        Assert.True(row.IsAccepted);
        Assert.Equal(200, row.StatusCode);
    }
}
