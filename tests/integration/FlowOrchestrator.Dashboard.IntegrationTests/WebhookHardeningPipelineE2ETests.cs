using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Dashboard.Webhooks;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// End-to-end integration tests that exercise the FULL webhook hardening
/// pipeline (signature → IP allowlist → rate limit → replay → dispatch)
/// through a real HTTP request, not gate-by-gate in isolation.
/// </summary>
/// <remarks>
/// Each test owns its own <see cref="DashboardTestServer"/> via <c>using var</c>.
/// Sharing a server through field-init triggers NSubstitute thread-local
/// last-call leakage when xUnit runs test classes in parallel — symptom:
/// "Could not find a call to return from" errors on the FIRST <c>.Returns()</c>
/// of every test method.
/// </remarks>
public sealed class WebhookHardeningPipelineE2ETests
{
    private static IFlowDefinition MakeGitHubWebhookFlow(Guid id, string secret)
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
                        ["webhookSlug"] = "github-events",
                        ["webhookHmacKey"] = secret,
                        ["webhookSignatureScheme"] = "GitHub",
                        ["webhookReplayToleranceSeconds"] = 300,
                        ["webhookNonceHeader"] = "X-GitHub-Delivery",
                    }
                }
            }
        });
        return flow;
    }

    private static string GitHubSignature(string secret, byte[] body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    private static DashboardTestServer NewEnforceServer() => new(o => o.UseWebhookSecurity(s => s
        .UseEnforcementMode(WebhookEnforcementMode.Enforce)
        .UseReplayProtection(toleranceSeconds: 300)
        .UseRateLimit(permitsPerSecond: 100, burstSize: 100)
        .UseMaxBodyBytes(64 * 1024)));

    [Fact]
    public async Task Full_pipeline_accepts_request_with_valid_signature_timestamp_and_delivery_id()
    {
        // Arrange
        using var server = NewEnforceServer();
        using var client = server.CreateClient();
        var id = Guid.NewGuid();
        const string secret = "ghs_test_secret";
        var flow = MakeGitHubWebhookFlow(id, secret);
        server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });
        var body = Encoding.UTF8.GetBytes("{\"event\":\"push\"}");

        using var req = new HttpRequestMessage(HttpMethod.Post, "/flows/api/webhook/github-events")
        {
            Content = new ByteArrayContent(body) { Headers = { { "Content-Type", "application/json" } } },
        };
        req.Headers.Add("X-Hub-Signature-256", GitHubSignature(secret, body));
        req.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString());
        req.Headers.Add("X-Webhook-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());

        // Act
        using var response = await client.SendAsync(req);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await server.FlowOrchestrator.Received(1).TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Full_pipeline_rejects_signature_mismatch_with_401()
    {
        // Arrange
        using var server = NewEnforceServer();
        using var client = server.CreateClient();
        var id = Guid.NewGuid();
        const string secret = "ghs_test_secret";
        var flow = MakeGitHubWebhookFlow(id, secret);
        server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });
        var body = Encoding.UTF8.GetBytes("{\"event\":\"push\"}");

        using var req = new HttpRequestMessage(HttpMethod.Post, "/flows/api/webhook/github-events")
        {
            Content = new ByteArrayContent(body) { Headers = { { "Content-Type", "application/json" } } },
        };
        req.Headers.Add("X-Hub-Signature-256", "sha256=" + new string('0', 64));
        req.Headers.Add("X-Webhook-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());

        // Act
        using var response = await client.SendAsync(req);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await server.FlowOrchestrator.DidNotReceive().TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Full_pipeline_rejects_replay_with_409_on_second_identical_delivery()
    {
        // Arrange
        using var server = NewEnforceServer();
        using var client = server.CreateClient();
        var id = Guid.NewGuid();
        const string secret = "ghs_test_secret";
        var flow = MakeGitHubWebhookFlow(id, secret);
        server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });
        var body = Encoding.UTF8.GetBytes("{\"event\":\"push\"}");
        var deliveryId = Guid.NewGuid().ToString();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        HttpRequestMessage Build()
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "/flows/api/webhook/github-events")
            {
                Content = new ByteArrayContent(body) { Headers = { { "Content-Type", "application/json" } } },
            };
            r.Headers.Add("X-Hub-Signature-256", GitHubSignature(secret, body));
            r.Headers.Add("X-GitHub-Delivery", deliveryId);
            r.Headers.Add("X-Webhook-Timestamp", ts);
            return r;
        }

        // Act
        var first = await client.SendAsync(Build());
        var second = await client.SendAsync(Build());

        // Assert
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Full_pipeline_rejects_oversized_body_with_413_before_signature_check()
    {
        // Arrange — body cap is 64 KiB; send 128 KiB.
        using var server = NewEnforceServer();
        using var client = server.CreateClient();
        var id = Guid.NewGuid();
        var oversizedFlow = MakeGitHubWebhookFlow(id, "secret");
        server.FlowRepository.GetAllFlowsAsync().Returns(new[] { oversizedFlow });
        server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });
        var body = new byte[128 * 1024];

        using var req = new HttpRequestMessage(HttpMethod.Post, "/flows/api/webhook/github-events")
        {
            Content = new ByteArrayContent(body) { Headers = { { "Content-Type", "application/octet-stream" } } },
        };

        // Act
        using var response = await client.SendAsync(req);

        // Assert
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Audit_mode_returns_2xx_even_when_signature_invalid_and_writes_dlq()
    {
        // Arrange — Audit mode never rejects.
        using var server = new DashboardTestServer(o => o.UseWebhookSecurity(s =>
            s.UseEnforcementMode(WebhookEnforcementMode.Audit)));
        using var client = server.CreateClient();
        var id = Guid.NewGuid();
        var auditFlow = MakeGitHubWebhookFlow(id, "secret");
        server.FlowRepository.GetAllFlowsAsync().Returns(new[] { auditFlow });
        server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });
        var body = Encoding.UTF8.GetBytes("{\"event\":\"push\"}");

        using var req = new HttpRequestMessage(HttpMethod.Post, "/flows/api/webhook/github-events")
        {
            Content = new ByteArrayContent(body) { Headers = { { "Content-Type", "application/json" } } },
        };
        req.Headers.Add("X-Hub-Signature-256", "sha256=" + new string('0', 64));

        // Act
        using var response = await client.SendAsync(req);

        // Assert
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Accepted,
            $"Expected 200/202 in Audit mode, got {response.StatusCode}");
        var dlq = server.Services.GetRequiredService<IWebhookRejectionStore>();
        var rows = await dlq.QueryRecentAsync(id, reason: null, includeAccepted: true, skip: 0, take: 50);
        Assert.Contains(rows, r => !r.IsAccepted);
    }

    [Fact]
    public async Task Off_mode_passes_through_bad_signature()
    {
        // Arrange — default Off mode skips every gate.
        using var server = new DashboardTestServer();
        using var client = server.CreateClient();
        var id = Guid.NewGuid();
        var offFlow = MakeGitHubWebhookFlow(id, "secret");
        server.FlowRepository.GetAllFlowsAsync().Returns(new[] { offFlow });
        server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });

        using var req = new HttpRequestMessage(HttpMethod.Post, "/flows/api/webhook/github-events")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")) { Headers = { { "Content-Type", "application/json" } } },
        };
        req.Headers.Add("X-Hub-Signature-256", "sha256=" + new string('0', 64));

        // Act
        using var response = await client.SendAsync(req);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Legacy_bearer_secret_still_works_alongside_pipeline_in_off_mode()
    {
        // Arrange — Off mode; flow only declares webhookSecret (no scheme).
        using var server = new DashboardTestServer();
        using var client = server.CreateClient();
        var id = Guid.NewGuid();
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
                        ["webhookSlug"] = "legacy-flow",
                        ["webhookSecret"] = "legacy-shared-secret",
                    }
                }
            }
        });
        server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });

        using var req = new HttpRequestMessage(HttpMethod.Post, "/flows/api/webhook/legacy-flow")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")) { Headers = { { "Content-Type", "application/json" } } },
        };
        req.Headers.Add("X-Webhook-Key", "legacy-shared-secret");

        // Act
        using var response = await client.SendAsync(req);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await server.FlowOrchestrator.Received(1).TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }
}
