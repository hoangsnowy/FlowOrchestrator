using System.Text;
using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.UnitTests.Signature;

/// <summary>Vector tests for Shopify's base64 SHA-256 over raw body.</summary>
public sealed class ShopifyVectorTests
{
    private static readonly WebhookSignatureSpec _spec =
        PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.Shopify)!;

    [Fact]
    public void Verify_succeeds_for_base64_signature_over_raw_body()
    {
        // Arrange
        const string key = "shopify_secret";
        var body = Encoding.UTF8.GetBytes("{\"order_id\":12345}");
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, key, body);
        var ctx = SignatureTestHelper.Context(_spec, body, new Dictionary<string, string>
        {
            ["X-Shopify-Hmac-SHA256"] = SignatureTestHelper.ToBase64(digest),
        }, key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Verify_rejects_invalid_base64()
    {
        // Arrange
        const string key = "shopify_secret";
        var body = Encoding.UTF8.GetBytes("payload");
        var ctx = SignatureTestHelper.Context(_spec, body, new Dictionary<string, string>
        {
            ["X-Shopify-Hmac-SHA256"] = "@@@not-base-64@@@",
        }, key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(WebhookSignatureFailureReason.DigestMismatch, result.Reason);
    }
}
