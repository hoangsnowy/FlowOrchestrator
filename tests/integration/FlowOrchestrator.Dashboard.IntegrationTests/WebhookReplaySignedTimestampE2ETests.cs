using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Dashboard.Webhooks;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Replay protection for schemes that sign their own timestamp (Stripe's <c>t=</c>) must judge freshness from
/// that signed value. An unsigned <c>X-Webhook-Timestamp</c> header is attacker-controlled: before the fix it
/// let a captured delivery be replayed indefinitely, and a genuine Stripe delivery (which never sends that
/// header) was rejected as <c>missing_timestamp</c>.
/// </summary>
public sealed class WebhookReplaySignedTimestampE2ETests
{
    private const string Secret = "whsec_replay_test";
    private const string Slug = "stripe-events";

    private static IFlowDefinition MakeStripeFlow(Guid id)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(id);
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["webhook"] = new TriggerMetadata
                {
                    Type = TriggerType.Webhook,
                    Inputs = new Dictionary<string, object?>
                    {
                        ["webhookSlug"] = Slug,
                        ["webhookHmacKey"] = Secret,
                        ["webhookSignatureScheme"] = "Stripe",
                        ["webhookReplayToleranceSeconds"] = 300,
                    }
                }
            }
        });
        return flow;
    }

    private static string StripeSignature(long t, byte[] body)
    {
        var payload = Encoding.UTF8.GetBytes(t.ToString(CultureInfo.InvariantCulture) + "." + Encoding.UTF8.GetString(body));
        var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), payload);
        return $"t={t},v1={Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static HttpRequestMessage Delivery(byte[] body, string stripeSignature, long? unsignedTimestamp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/flows/api/webhook/{Slug}")
        {
            Content = new ByteArrayContent(body) { Headers = { { "Content-Type", "application/json" } } },
        };
        request.Headers.Add("Stripe-Signature", stripeSignature);
        if (unsignedTimestamp is { } ts)
        {
            request.Headers.Add("X-Webhook-Timestamp", ts.ToString(CultureInfo.InvariantCulture));
        }
        return request;
    }

    private static (DashboardTestServer Server, HttpClient Client) NewServer()
    {
        var server = new DashboardTestServer(o => o.UseWebhookSecurity(s => s
            .UseEnforcementMode(WebhookEnforcementMode.Enforce)
            .UseReplayProtection(toleranceSeconds: 300)));
        var id = Guid.NewGuid();
        // Build the flow substitute before configuring another one: nesting it inside .Returns(...) breaks
        // NSubstitute's last-call tracking.
        var flow = MakeStripeFlow(id);
        server.Flows = new FlowRegistry(new[] { flow });
        server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });
        return (server, server.CreateClient());
    }

    [Fact]
    public async Task Genuine_stripe_delivery_without_a_timestamp_header_is_accepted()
    {
        // Arrange
        var (server, client) = NewServer();
        using var _ = server;
        using var __ = client;
        var body = Encoding.UTF8.GetBytes("{\"id\":\"evt_genuine\"}");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Act
        using var response = await client.SendAsync(Delivery(body, StripeSignature(now, body), unsignedTimestamp: null));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Captured_stripe_delivery_replayed_with_a_forged_fresh_timestamp_header_is_rejected()
    {
        // Arrange — the delivery was signed an hour ago; the attacker adds a fresh unsigned header.
        var (server, client) = NewServer();
        using var _ = server;
        using var __ = client;
        var body = Encoding.UTF8.GetBytes("{\"id\":\"evt_captured\"}");
        var signedAnHourAgo = StripeSignature(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(), body);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Act
        using var replay = await client.SendAsync(Delivery(body, signedAnHourAgo, unsignedTimestamp: now));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
    }

    [Fact]
    public async Task Fresh_stripe_delivery_replayed_with_a_different_timestamp_header_is_rejected()
    {
        // Arrange — inside the tolerance window, varying the unsigned header must not mint a new replay key.
        var (server, client) = NewServer();
        using var _ = server;
        using var __ = client;
        var body = Encoding.UTF8.GetBytes("{\"id\":\"evt_fresh\"}");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = StripeSignature(now, body);

        // Act
        using var first = await client.SendAsync(Delivery(body, signature, unsignedTimestamp: now));
        using var replay = await client.SendAsync(Delivery(body, signature, unsignedTimestamp: now - 7));

        // Assert
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
    }
}
