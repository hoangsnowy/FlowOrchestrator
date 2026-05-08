using System.Text;
using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.UnitTests.Signature;

/// <summary>Vector tests for the Stripe <c>Stripe-Signature</c> multi-value dialect.</summary>
public sealed class StripeVectorTests
{
    private static readonly WebhookSignatureSpec _spec =
        PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.Stripe)!;

    [Fact]
    public void Verify_succeeds_for_valid_t_v1_pair()
    {
        // Arrange
        const string key = "whsec_test";
        const string ts = "1700000000";
        var body = Encoding.UTF8.GetBytes("{\"id\":\"evt_test\"}");
        var signed = Encoding.UTF8.GetBytes(ts + ".").Concat(body).ToArray();
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, key, signed);
        var header = $"t={ts},v1={SignatureTestHelper.ToHex(digest)}";
        var ctx = SignatureTestHelper.Context(_spec, body,
            new Dictionary<string, string> { ["Stripe-Signature"] = header }, key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Verify_accepts_v1_when_v0_is_also_present()
    {
        // Arrange
        const string key = "whsec_test";
        const string ts = "1700000000";
        var body = Encoding.UTF8.GetBytes("payload");
        var signed = Encoding.UTF8.GetBytes(ts + ".").Concat(body).ToArray();
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, key, signed);
        // v0 placeholder + valid v1
        var header = $"t={ts},v0=00,v1={SignatureTestHelper.ToHex(digest)}";
        var ctx = SignatureTestHelper.Context(_spec, body,
            new Dictionary<string, string> { ["Stripe-Signature"] = header }, key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Verify_rejects_when_no_v1_in_header()
    {
        // Arrange
        const string key = "k";
        const string ts = "1700000000";
        var body = Encoding.UTF8.GetBytes("p");
        var ctx = SignatureTestHelper.Context(_spec, body,
            new Dictionary<string, string> { ["Stripe-Signature"] = $"t={ts},v0=deadbeef" }, key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(WebhookSignatureFailureReason.UnsupportedVersion, result.Reason);
    }

    [Fact]
    public void Verify_rejects_when_timestamp_missing()
    {
        // Arrange
        const string key = "k";
        var body = Encoding.UTF8.GetBytes("p");
        var ctx = SignatureTestHelper.Context(_spec, body,
            new Dictionary<string, string> { ["Stripe-Signature"] = "v1=deadbeef" }, key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(WebhookSignatureFailureReason.MissingTimestamp, result.Reason);
    }
}
