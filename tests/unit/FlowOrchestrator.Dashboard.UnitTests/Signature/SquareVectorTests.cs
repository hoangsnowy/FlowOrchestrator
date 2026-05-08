using System.Text;
using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.UnitTests.Signature;

/// <summary>Vector tests for Square (SHA-256 base64 over <c>{url}{body}</c>).</summary>
public sealed class SquareVectorTests
{
    private static readonly WebhookSignatureSpec _spec =
        PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.Square)!;

    [Fact]
    public void Verify_succeeds_for_url_plus_body()
    {
        // Arrange
        const string key = "square_signing_key";
        const string url = "https://example.com/square/notifications";
        var body = Encoding.UTF8.GetBytes("{\"event\":\"payment.created\"}");
        var payload = Encoding.UTF8.GetBytes(url).Concat(body).ToArray();
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, key, payload);
        var ctx = SignatureTestHelper.Context(_spec, body,
            new Dictionary<string, string> { ["X-Square-HmacSha256-Signature"] = SignatureTestHelper.ToBase64(digest) },
            key, absoluteUrl: url);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid);
    }
}
