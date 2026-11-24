using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Dashboard.Webhooks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Audit (4th sweep) — dashboard REST + webhook defects proven through real HTTP requests against
/// <see cref="DashboardTestServer"/>. Each test failed on the audited commit; the summary of each states
/// the production impact and the expected behaviour.
/// </summary>
/// <remarks>
/// Each test owns its server (<c>using var</c>) to avoid NSubstitute last-call leakage across
/// parallel test classes.
/// </remarks>
public sealed class DashboardSurfaceAuditTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static IFlowDefinition WebhookFlow(Guid id, Dictionary<string, object?> inputs, string triggerKey = "webhook")
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(id);
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                [triggerKey] = new TriggerMetadata { Type = TriggerType.Webhook, Inputs = inputs }
            },
            Steps = new StepCollection { ["s"] = new StepMetadata { Type = "Noop" } }
        });
        return flow;
    }

    private static void RegisterEnabled(DashboardTestServer server, IFlowDefinition flow)
    {
        // Read the substitute's Id BEFORE arranging another substitute: NSubstitute would otherwise
        // attach the Returns() below to flow.Id.
        var id = flow.Id;
        server.Flows = new FlowRegistry(new[] { flow });
        server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });
    }

    private static HttpRequestMessage JsonPost(string url, byte[] body)
        => new(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(body) { Headers = { { "Content-Type", "application/json" } } },
        };

    private static string GitHubSignature(string key, byte[] body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), body)).ToLowerInvariant();

    // ── Webhook signature dialects ───────────────────────────────────────────────────────────

    /// <summary>
    /// The MicrosoftTeams spec reads its signature from the <c>Authorization</c> header, which the endpoint
    /// strips from the captured headers. The verifier must still see it, or no Teams delivery can ever be
    /// accepted in Enforce mode (the 2.0 default). Signed the way Microsoft documents it: HMAC-SHA256 keyed
    /// with the base64-decoded security token.
    /// </summary>
    [Fact]
    public async Task MicrosoftTeams_scheme_accepts_correctly_signed_delivery()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var id = Guid.NewGuid();
        var securityToken = Convert.ToBase64String(Encoding.UTF8.GetBytes("teams-signing-key-bytes"));
        RegisterEnabled(server, WebhookFlow(id, new Dictionary<string, object?>
        {
            ["webhookSlug"] = "teams",
            ["webhookHmacKey"] = securityToken,
            ["webhookSignatureScheme"] = "MicrosoftTeams",
        }));
        var body = Encoding.UTF8.GetBytes("{\"type\":\"message\",\"text\":\"hi\"}");
        using var request = JsonPost("/flows/api/webhook/teams", body);
        request.Headers.TryAddWithoutValidation("Authorization",
            "HMAC " + Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(securityToken), body)));

        // Act
        using var response = await server.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The Authorization header handed to the verifier must not leak into the trigger headers the run
    /// stores (and run detail serves).
    /// </summary>
    [Fact]
    public async Task MicrosoftTeams_delivery_does_not_capture_the_Authorization_header_as_a_trigger_header()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var id = Guid.NewGuid();
        var securityToken = Convert.ToBase64String(Encoding.UTF8.GetBytes("teams-signing-key-bytes"));
        RegisterEnabled(server, WebhookFlow(id, new Dictionary<string, object?>
        {
            ["webhookSlug"] = "teams",
            ["webhookHmacKey"] = securityToken,
            ["webhookSignatureScheme"] = "MicrosoftTeams",
        }));
        ITriggerContext? captured = null;
        server.FlowOrchestrator.TriggerAsync(Arg.Do<ITriggerContext>(c => captured = c), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<object?>((object?)null));
        var body = Encoding.UTF8.GetBytes("{\"type\":\"message\"}");
        using var request = JsonPost("/flows/api/webhook/teams", body);
        request.Headers.TryAddWithoutValidation("Authorization",
            "HMAC " + Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(securityToken), body)));

        // Act
        using var response = await server.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(captured);
        Assert.False(captured!.Trigger.Headers!.ContainsKey("Authorization"));
    }
}
