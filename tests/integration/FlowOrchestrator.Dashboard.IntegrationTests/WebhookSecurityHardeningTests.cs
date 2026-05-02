using System.Net;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Hardening tests for the webhook auth surface. Covers:
///   <list type="bullet">
///     <item>Slug case-insensitivity (matches the existing lookup behaviour).</item>
///     <item>Secret case-sensitivity (secrets are credentials, not identifiers).</item>
///     <item>Lowercase <c>bearer</c> prefix in the Authorization header.</item>
///     <item>Structural regression for the timing-attack fix — wrong keys
///       of any length must take the same SecureEquals path and return 401
///       without leaking the prefix.</item>
///   </list>
/// </summary>
public sealed class WebhookSecurityHardeningTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;

    public WebhookSecurityHardeningTests() => _client = _server.CreateClient();

    public void Dispose() => _server.Dispose();

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

    [Fact]
    public async Task SlugLookup_IsCaseInsensitive()
    {
        // Arrange — registered slug is mixed-case; request uses different case.
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id, slug: "Order-Created");
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });

        // Act
        var response = await _client.PostAsync("/flows/api/webhook/order-created", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SecretComparison_IsCaseSensitive()
    {
        // Arrange
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id, secret: "S3cret-V@lue");
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/flows/api/webhook/{id}");
        request.Headers.Add("X-Webhook-Key", "s3cret-v@lue"); // lowercased — must NOT pass

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LowercaseBearerPrefix_IsAccepted()
    {
        // Arrange — the handler trims "Bearer " case-insensitively. Common
        // clients (curl, Postman) emit "bearer " in lowercase under various
        // configurations.
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id, secret: "tok-abcdef");
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/flows/api/webhook/{id}");
        request.Headers.Add("Authorization", "bearer tok-abcdef");

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WrongSecretsOfDifferentLengths_BothReturn401_NoTimingLeak()
    {
        // Structural regression for the timing-attack fix at
        // DashboardServiceCollectionExtensions.cs:283. The webhook handler now
        // delegates to SecureEquals (CryptographicOperations.FixedTimeEquals
        // over UTF-8 bytes). What we can verify deterministically in xUnit:
        // - All wrong keys produce 401 (i.e., no fallthrough on any prefix).
        // - The handler doesn't crash on empty / extreme-length keys.
        // We do NOT attempt to assert wall-clock equivalence — that's banned
        // per qa-agent.md anti-flakiness rules and not observable from
        // in-process xUnit anyway. The fix correctness is guaranteed by
        // FixedTimeEquals' API contract.

        // Arrange
        var id = Guid.NewGuid();
        var flow = MakeWebhookFlow(id, secret: "the-real-secret-value-32-chars__");
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });

        var wrongKeys = new[]
        {
            "a",                                     // wrong on byte 0
            "the",                                   // matches first 3 bytes
            "the-real-secret-value-32-chars_!",      // matches all but last byte
            "completely-different-32-char-key__",    // same length, all wrong
            "",                                      // empty
        };

        // Act + Assert
        foreach (var key in wrongKeys)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/flows/api/webhook/{id}");
            if (!string.IsNullOrEmpty(key))
            {
                request.Headers.Add("X-Webhook-Key", key);
            }
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }
}
