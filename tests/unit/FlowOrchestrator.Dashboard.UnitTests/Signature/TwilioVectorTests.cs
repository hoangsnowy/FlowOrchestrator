using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.UnitTests.Signature;

/// <summary>Vector tests for Twilio (SHA-1 base64 over <c>{url}{sortedForm}</c>).</summary>
public sealed class TwilioVectorTests
{
    private static readonly WebhookSignatureSpec _spec =
        PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.Twilio)!;

    [Fact]
    public void Verify_succeeds_with_url_and_sorted_form_fields()
    {
        // Arrange
        const string key = "AuthToken123";
        const string url = "https://example.com/twilio/webhook";
        var fields = new Dictionary<string, string>
        {
            ["From"] = "+15551234567",
            ["To"] = "+15557654321",
            ["Body"] = "Hello",
        };
        // Twilio composes signed payload as URL + sorted-by-key concat of "{key}{value}".
        var sorted = string.Concat(fields.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                          .Select(kv => kv.Key + kv.Value));
        var payload = System.Text.Encoding.UTF8.GetBytes(url + sorted);
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha1, key, payload);
        var ctx = SignatureTestHelper.Context(_spec, Array.Empty<byte>(),
            new Dictionary<string, string> { ["X-Twilio-Signature"] = SignatureTestHelper.ToBase64(digest) },
            key, absoluteUrl: url, formFields: fields, allowLegacySha1: true);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Verify_rejects_when_url_missing()
    {
        // Arrange
        var ctx = SignatureTestHelper.Context(_spec, Array.Empty<byte>(),
            new Dictionary<string, string> { ["X-Twilio-Signature"] = "Y29tcHV0ZWQ=" },
            "key", formFields: new Dictionary<string, string>(), allowLegacySha1: true);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(WebhookSignatureFailureReason.AbsoluteUrlRequired, result.Reason);
    }
}
