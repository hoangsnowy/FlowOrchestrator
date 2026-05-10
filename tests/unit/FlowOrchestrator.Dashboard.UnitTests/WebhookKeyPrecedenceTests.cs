using System.Text;
using FlowOrchestrator.Dashboard.UnitTests.Signature;
using FlowOrchestrator.Dashboard.Webhooks;
using FlowOrchestrator.Dashboard.Webhooks.Signature;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Dashboard.UnitTests;

/// <summary>
/// Pins the v1.24 → v1.25 migration contract for webhook HMAC key fields:
/// <list type="bullet">
///   <item>only <c>webhookSecret</c> set → still verifies, no conflict warning;</item>
///   <item>both fields set → <c>webhookHmacKey</c> wins, EventId 4011 fires once per flow;</item>
///   <item>both fields set + signed with the legacy value → reject because the modern key is the active one.</item>
/// </list>
/// </summary>
public sealed class WebhookKeyPrecedenceTests
{
    private static readonly WebhookSignatureSpec _githubSpec =
        PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.GitHub)!;

    [Fact]
    public async Task OnlyLegacySecret_StillVerifies_NoConflictWarning()
    {
        // Arrange
        const string secret = "v124-key";
        var body = Encoding.UTF8.GetBytes("hello");
        var digest = "sha256=" + SignatureTestHelper.ToHex(
            SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, secret, body));
        var inputs = new Dictionary<string, object?>
        {
            ["webhookSignatureScheme"] = "GitHub",
            ["webhookSecret"] = secret,
        };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Hub-Signature-256"] = digest,
        };
        var logger = new RecordingLogger<WebhookSecurityPipeline>();
        var pipeline = NewPipeline(logger);

        // Act
        var result = await pipeline.EvaluateAsync(NewHttpContext(), Guid.NewGuid(), "wh", inputs, body, headers);

        // Assert
        Assert.Equal(WebhookSecurityPipeline.Decision.Accept, result.Decision);
        Assert.DoesNotContain(logger.Records, r => r.EventId.Id == 4011);
    }

    [Fact]
    public async Task BothFieldsPresent_HmacKeyWins_LogsEventId4011_OncePerFlow()
    {
        // Arrange
        const string modern = "v125-key";
        const string legacy = "v124-key";
        var body = Encoding.UTF8.GetBytes("hello");
        var digest = "sha256=" + SignatureTestHelper.ToHex(
            SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, modern, body));
        var inputs = new Dictionary<string, object?>
        {
            ["webhookSignatureScheme"] = "GitHub",
            ["webhookHmacKey"] = modern,
            ["webhookSecret"] = legacy,
        };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Hub-Signature-256"] = digest,
        };
        var flowId = Guid.NewGuid();
        var logger = new RecordingLogger<WebhookSecurityPipeline>();
        var pipeline = NewPipeline(logger);

        // Act — replay 5 times for the same flow.
        for (var i = 0; i < 5; i++)
        {
            var r = await pipeline.EvaluateAsync(NewHttpContext(), flowId, "wh", inputs, body, headers);
            Assert.Equal(WebhookSecurityPipeline.Decision.Accept, r.Decision);
        }

        // Assert — exactly one EventId 4011 emitted across the five hits.
        Assert.Equal(1, logger.Records.Count(r => r.EventId.Id == 4011));
    }

    [Fact]
    public async Task BothFieldsPresent_SignedWithLegacy_RejectsBecauseHmacKeyWins()
    {
        // Arrange
        const string modern = "v125-key";
        const string legacy = "v124-key";
        var body = Encoding.UTF8.GetBytes("hello");
        var legacyDigest = "sha256=" + SignatureTestHelper.ToHex(
            SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, legacy, body));
        var inputs = new Dictionary<string, object?>
        {
            ["webhookSignatureScheme"] = "GitHub",
            ["webhookHmacKey"] = modern,
            ["webhookSecret"] = legacy,
        };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Hub-Signature-256"] = legacyDigest,
        };
        var pipeline = NewPipeline();

        // Act
        var result = await pipeline.EvaluateAsync(NewHttpContext(), Guid.NewGuid(), "wh", inputs, body, headers);

        // Assert — the modern key is the canonical one; legacy-signed digest must be rejected.
        Assert.Equal(WebhookSecurityPipeline.Decision.Reject, result.Decision);
        Assert.Contains("DigestMismatch", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpContext NewHttpContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("example.test");
        return ctx;
    }

    private static WebhookSecurityPipeline NewPipeline(ILogger<WebhookSecurityPipeline>? logger = null)
    {
        var options = new WebhookSecurityOptions { EnforcementMode = WebhookEnforcementMode.Enforce };
        var verifier = new HmacSignatureVerifier();
        return new WebhookSecurityPipeline(verifier, options, logger: logger);
    }

    /// <summary>
    /// Capture-only logger. Avoids the NSubstitute generic-method matching
    /// gotcha when asserting source-generated <c>LoggerMessage</c> calls.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, EventId EventId, string Message)> Records = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Records.Add((logLevel, eventId, formatter(state, exception)));
        }
    }
}
