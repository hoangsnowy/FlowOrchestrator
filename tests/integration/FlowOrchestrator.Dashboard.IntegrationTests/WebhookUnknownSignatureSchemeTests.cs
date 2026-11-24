using System.Globalization;
using System.Net;
using System.Text;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Dashboard.Webhooks;
using FlowOrchestrator.Dashboard.Webhooks.Signature;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// A webhook trigger that names a signature scheme nothing can verify — a typo, an unsupported provider, or
/// <c>Custom</c> with an unregistered payload strategy — must never fall through to "no signature required".
/// At request time it is rejected in <see cref="WebhookEnforcementMode.Enforce"/> and recorded as a violation in
/// <see cref="WebhookEnforcementMode.Audit"/>; at startup <c>MapFlowDashboard</c> refuses such a flow.
/// </summary>
/// <remarks>Each test owns its server (<c>using var</c>) so NSubstitute arrangements never leak across tests.</remarks>
public sealed class WebhookUnknownSignatureSchemeTests
{
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static IFlowDefinition WebhookFlow(string slug, IReadOnlyDictionary<string, object?> inputs)
    {
        var id = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(id);
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["webhook"] = new TriggerMetadata { Type = TriggerType.Webhook, Inputs = new Dictionary<string, object?>(inputs) { ["webhookSlug"] = slug } },
            },
        });
        return flow;
    }

    private static Guid Register(DashboardTestServer server, string slug, IReadOnlyDictionary<string, object?> inputs)
    {
        var flow = WebhookFlow(slug, inputs);
        var id = flow.Id;
        server.Flows = new FlowRegistry([flow]);
        server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });
        return id;
    }

    private static DashboardTestServer Server(WebhookEnforcementMode mode, Action<WebhookSecurityOptionsBuilder>? configure = null)
        => DashboardTestServer.WithServices(s => s.AddFlowDashboard(o => o.UseWebhookSecurity(w =>
        {
            w.UseEnforcementMode(mode);
            configure?.Invoke(w);
        })));

    private static HttpRequestMessage Delivery(string slug, string body, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/flows/api/webhook/{slug}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        return request;
    }

    private static async Task<IReadOnlyList<WebhookRejectionRecord>> LogAsync(DashboardTestServer server, Guid flowId)
        => (await server.Services.GetRequiredService<IWebhookRejectionStore>()
            .QueryAsync(new WebhookRejectionQuery(flowId, null, null, IncludeAccepted: true, IncludeRejected: true, Skip: 0, Take: 50))).Items;

    [Fact]
    public async Task Unknown_signature_scheme_name_fails_closed()
    {
        // Arrange — "Svix" is not a built-in scheme; before, the signature gate was skipped and a forged,
        // unsigned delivery started a run on an endpoint the operator believed was signed.
        using var server = Server(WebhookEnforcementMode.Enforce);
        var flowId = Register(server, "svix-events", new Dictionary<string, object?>
        {
            ["webhookSignatureScheme"] = "Svix",
            ["webhookHmacKey"] = "whsec_" + "MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw",
        });
        using var request = Delivery("svix-events", "{\"test\": 1}",
            ("webhook-id", "msg_forged"), ("webhook-timestamp", Now.ToString(CultureInfo.InvariantCulture)),
            ("webhook-signature", "v1,AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="));

        // Act
        using var response = await server.Client.SendAsync(request);
        var rows = await LogAsync(server, flowId);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await server.FlowOrchestrator.DidNotReceive().TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
        var row = Assert.Single(rows);
        Assert.False(row.IsAccepted);
        Assert.Equal("signature_scheme_unknown", row.Reason);
        Assert.Equal(401, row.StatusCode);
    }

    [Fact]
    public async Task StandardWebhooks_tampered_vector_is_rejected()
    {
        // Arrange — the Standard Webhooks library's own "invalid signature" case (last base64 char changed).
        // The built-in StandardWebhooks dialect verifies it and rejects the digest mismatch.
        using var server = Server(WebhookEnforcementMode.Enforce);
        Register(server, "stdwh-events", new Dictionary<string, object?>
        {
            ["webhookSignatureScheme"] = "StandardWebhooks",
            ["webhookHmacKey"] = "whsec_" + "MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw",
        });
        using var request = Delivery("stdwh-events", "{\"test\": 2432232314}",
            ("webhook-id", "msg_p5jXN8AQM9LWM0D4loKWxJek"),
            ("webhook-timestamp", "1614265330"),
            ("webhook-signature", "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OA="));

        // Act
        using var response = await server.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await server.FlowOrchestrator.DidNotReceive().TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Custom_scheme_with_an_unregistered_payload_strategy_is_rejected_as_unknown()
    {
        // Arrange
        using var server = Server(WebhookEnforcementMode.Enforce);
        var flowId = Register(server, "custom-events", new Dictionary<string, object?>
        {
            ["webhookSignatureScheme"] = "Custom",
            ["webhookSignatureHeader"] = "X-Sig",
            ["webhookSignedPayloadStrategy"] = "Custom",
            ["webhookCustomStrategyName"] = "never-registered",
            ["webhookHmacKey"] = "k",
        });
        using var request = Delivery("custom-events", "{}", ("X-Sig", "00"));

        // Act
        using var response = await server.Client.SendAsync(request);
        var rows = await LogAsync(server, flowId);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("signature_scheme_unknown", Assert.Single(rows).Reason);
    }

    [Fact]
    public async Task Audit_mode_records_the_unknown_scheme_as_a_violation_without_blocking()
    {
        // Arrange — Audit never blocks, by definition; the violation must still be visible.
        using var server = Server(WebhookEnforcementMode.Audit);
        var flowId = Register(server, "svix-events", new Dictionary<string, object?>
        {
            ["webhookSignatureScheme"] = "Svix",
            ["webhookHmacKey"] = "k",
        });
        using var request = Delivery("svix-events", "{}");

        // Act
        using var response = await server.Client.SendAsync(request);
        var rows = await LogAsync(server, flowId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single(rows);
        Assert.False(row.IsAccepted);
        Assert.Equal("signature_scheme_unknown", row.Reason);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("")]
    public async Task No_scheme_or_explicit_None_keeps_working_unsigned(string scheme)
    {
        // Arrange — "None" is the documented way to say a trigger is not signed.
        using var server = Server(WebhookEnforcementMode.Enforce);
        Register(server, "plain-events", new Dictionary<string, object?> { ["webhookSignatureScheme"] = scheme });
        using var request = Delivery("plain-events", "{}");

        // Act
        using var response = await server.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("Svix")]
    [InlineData("Custom")]
    public void Registered_flow_naming_an_unverifiable_scheme_fails_at_startup(string scheme)
    {
        // Arrange — "Custom" here selects the Custom payload strategy with no registered strategy.
        var flow = WebhookFlow("bad-events", new Dictionary<string, object?>
        {
            ["webhookSignatureScheme"] = scheme,
            ["webhookSignatureHeader"] = "X-Sig",
            ["webhookSignedPayloadStrategy"] = "Custom",
            ["webhookCustomStrategyName"] = "never-registered",
        });

        // Act
        var ex = Record.Exception(() => DashboardTestServer.WithServices(s =>
        {
            s.AddSingleton(new FlowRegistry([flow]));
            s.AddFlowDashboard(o => o.UseWebhookSecurity(w => w.UseEnforcementMode(WebhookEnforcementMode.Enforce)));
        }));

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("webhookSignatureScheme", invalid.Message);
        Assert.Contains($"'{scheme}'", invalid.Message);
    }

    [Fact]
    public void Registered_flows_with_verifiable_schemes_start_normally()
    {
        // Arrange — built-in schemes (incl. StandardWebhooks, used by the sample app), a scheme registered with
        // RegisterScheme, and Off mode with a typo.
        var builtIn = WebhookFlow("gh", new Dictionary<string, object?> { ["webhookSignatureScheme"] = "GitHub" });
        var standardWebhooks = WebhookFlow("stdwh", new Dictionary<string, object?> { ["webhookSignatureScheme"] = "StandardWebhooks" });
        var registered = WebhookFlow("acme", new Dictionary<string, object?> { ["webhookSignatureScheme"] = "Acme" });
        var typo = WebhookFlow("typo", new Dictionary<string, object?> { ["webhookSignatureScheme"] = "GitHuub" });

        // Act
        var enforce = Record.Exception(() =>
        {
            using var server = DashboardTestServer.WithServices(s =>
            {
                s.AddSingleton(new FlowRegistry([builtIn, standardWebhooks, registered]));
                s.AddFlowDashboard(o => o.UseWebhookSecurity(w => w
                    .UseEnforcementMode(WebhookEnforcementMode.Enforce)
                    .RegisterScheme("Acme", new WebhookSignatureSpec { HeaderName = "X-Acme-Signature" })));
            });
        });
        var off = Record.Exception(() =>
        {
            using var server = DashboardTestServer.WithServices(s =>
            {
                s.AddSingleton(new FlowRegistry([typo]));
                s.AddFlowDashboard(o => o.UseWebhookSecurity(w => w.UseEnforcementMode(WebhookEnforcementMode.Off)));
            });
        });

        // Assert
        Assert.Null(enforce);
        Assert.Null(off);
    }
}
