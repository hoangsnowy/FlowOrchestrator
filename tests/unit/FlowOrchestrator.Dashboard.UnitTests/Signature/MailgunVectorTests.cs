using System.Text;
using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.UnitTests.Signature;

/// <summary>Vector tests for Mailgun (signature lives in JSON body; HMAC over <c>{ts}{token}</c>).</summary>
public sealed class MailgunVectorTests
{
    private static readonly WebhookSignatureSpec _spec =
        PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.Mailgun)!;

    [Fact]
    public void Verify_succeeds_with_body_resident_signature()
    {
        // Arrange
        const string key = "mg_api_key";
        const string ts = "1700000000";
        const string token = "abcdef0123456789";
        var signedBytes = Encoding.UTF8.GetBytes(ts + token);
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, key, signedBytes);
        var hex = SignatureTestHelper.ToHex(digest);
        var bodyJson = $"{{\"signature\":{{\"timestamp\":\"{ts}\",\"token\":\"{token}\",\"signature\":\"{hex}\"}}}}";
        var ctx = SignatureTestHelper.Context(_spec, Encoding.UTF8.GetBytes(bodyJson),
            new Dictionary<string, string>(), key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Verify_rejects_when_body_signature_missing_field()
    {
        // Arrange
        const string key = "mg_api_key";
        var bodyJson = "{\"signature\":{\"timestamp\":\"1700000000\"}}";
        var ctx = SignatureTestHelper.Context(_spec, Encoding.UTF8.GetBytes(bodyJson),
            new Dictionary<string, string>(), key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.False(result.IsValid);
    }
}
