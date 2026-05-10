using System.Text;
using FlowOrchestrator.Dashboard;
using FlowOrchestrator.Dashboard.Webhooks;
using FlowOrchestrator.Dashboard.Webhooks.Signature;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Dashboard.UnitTests.Signature;

/// <summary>
/// Pins the v1.25.1 contract for <c>AddWebhookSignatureVerifier&lt;T&gt;</c>:
/// <list type="bullet">
///   <item>DI-registered verifiers receive the inbound signature context with <c>Spec = null</c>;</item>
///   <item>built-in scheme names continue to route through the HMAC verifier when DI verifiers exist for unrelated names;</item>
///   <item>scheme-name validation rejects collisions with built-in <see cref="WebhookSignatureScheme"/> values, the <c>Custom</c> sentinel, and null/whitespace.</item>
/// </list>
/// </summary>
public sealed class CustomVerifierDispatchTests
{
    [Fact]
    public async Task RegisteredCustomVerifier_IsInvoked_WithExpectedContext()
    {
        // Arrange
        var state = new VerifierState
        {
            ResultToReturn = WebhookSignatureResult.Success,
        };
        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddWebhookSignatureVerifier<FakeVerifier>("AcmeCorp");
        await using var sp = services.BuildServiceProvider();
        var pipeline = NewPipeline(sp);

        var body = Encoding.UTF8.GetBytes("hello");
        var inputs = new Dictionary<string, object?>
        {
            ["webhookSignatureScheme"] = "AcmeCorp",
            ["webhookHmacKey"] = "ignored-by-fake",
        };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Acme-Signature"] = "anything",
        };

        // Act
        var result = await pipeline.EvaluateAsync(NewHttpContext(), Guid.NewGuid(), "wh", inputs, body, headers);

        // Assert
        Assert.Equal(WebhookSecurityPipeline.Decision.Accept, result.Decision);
        Assert.Equal(1, state.InvokeCount);
        Assert.NotNull(state.Captured);
        Assert.Equal(body, state.Captured!.Body.ToArray());
        Assert.Equal("anything", state.Captured.Headers["X-Acme-Signature"]);
        Assert.Equal("https://example.test/", state.Captured.AbsoluteUrl);
        Assert.Null(state.Captured.Spec);
        Assert.Equal("ignored-by-fake", state.Captured.HmacKey);
    }

    [Fact]
    public async Task BuiltInScheme_GitHub_StillRoutesThroughHmacVerifier_EvenWithDiVerifierRegistered()
    {
        // Arrange
        var state = new VerifierState();
        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddWebhookSignatureVerifier<FakeVerifier>("AcmeCorp");
        await using var sp = services.BuildServiceProvider();
        var pipeline = NewPipeline(sp);

        const string secret = "github-secret";
        var body = Encoding.UTF8.GetBytes("hello github");
        var digest = "sha256=" + SignatureTestHelper.ToHex(
            SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, secret, body));
        var inputs = new Dictionary<string, object?>
        {
            ["webhookSignatureScheme"] = "GitHub",
            ["webhookHmacKey"] = secret,
        };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Hub-Signature-256"] = digest,
        };

        // Act
        var result = await pipeline.EvaluateAsync(NewHttpContext(), Guid.NewGuid(), "wh", inputs, body, headers);

        // Assert
        Assert.Equal(WebhookSecurityPipeline.Decision.Accept, result.Decision);
        Assert.Equal(0, state.InvokeCount);
    }

    [Theory]
    [InlineData("GitHub")]
    [InlineData("github")]
    [InlineData("Stripe")]
    [InlineData("STRIPE")]
    [InlineData("Slack")]
    [InlineData("Mailgun")]
    public void AddWebhookSignatureVerifier_ThrowsOnBuiltInName(string schemeName)
    {
        // Arrange
        var services = new ServiceCollection();

        // Act + Assert
        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddWebhookSignatureVerifier<FakeVerifier>(schemeName));
        Assert.Equal("schemeName", ex.ParamName);
    }

    [Fact]
    public void AddWebhookSignatureVerifier_ThrowsOnCustomSentinel()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act + Assert — both casings of the Custom sentinel collide with the enum.
        Assert.Throws<ArgumentException>(() =>
            services.AddWebhookSignatureVerifier<FakeVerifier>("Custom"));
        Assert.Throws<ArgumentException>(() =>
            services.AddWebhookSignatureVerifier<FakeVerifier>("custom"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddWebhookSignatureVerifier_ThrowsOnNullOrWhitespace(string? schemeName)
    {
        // Arrange
        var services = new ServiceCollection();

        // Act + Assert — ArgumentException OR its ArgumentNullException subclass for the null case.
        Assert.ThrowsAny<ArgumentException>(() =>
            services.AddWebhookSignatureVerifier<FakeVerifier>(schemeName!));
    }

    private static HttpContext NewHttpContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("example.test");
        ctx.Request.Path = "/";
        return ctx;
    }

    private static WebhookSecurityPipeline NewPipeline(IServiceProvider sp)
    {
        var registry = sp.GetRequiredService<WebhookCustomVerifierRegistry>();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var options = new WebhookSecurityOptions { EnforcementMode = WebhookEnforcementMode.Enforce };
        return new WebhookSecurityPipeline(
            new HmacSignatureVerifier(),
            options,
            customRegistry: registry,
            scopeFactory: scopeFactory);
    }

    /// <summary>Captures verifier invocations for assertion.</summary>
    private sealed class VerifierState
    {
        public WebhookSignatureContext? Captured;
        public int InvokeCount;
        public WebhookSignatureResult ResultToReturn = WebhookSignatureResult.Success;
    }

    /// <summary>
    /// Test-only verifier resolved via DI keyed-services. Records every call into
    /// the shared <see cref="VerifierState"/> singleton so tests can introspect the
    /// inbound <see cref="WebhookSignatureContext"/>.
    /// </summary>
    private sealed class FakeVerifier(VerifierState state) : IWebhookSignatureVerifier
    {
        public WebhookSignatureResult Verify(WebhookSignatureContext context)
        {
            state.Captured = context;
            state.InvokeCount++;
            return state.ResultToReturn;
        }
    }
}
