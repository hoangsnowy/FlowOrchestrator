using System.Text;
using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.UnitTests.Signature;

/// <summary>
/// Vector tests for the simpler raw-body schemes that share an identical shape:
/// Bitbucket, Linear, Dropbox, Atlassian, Calendly, Zoom, Generic.
/// </summary>
public sealed class BasicSchemesVectorTests
{
    [Theory]
    [InlineData(WebhookSignatureScheme.Bitbucket, "X-Hub-Signature", "sha256=")]
    [InlineData(WebhookSignatureScheme.Atlassian, "X-Hub-Signature", "sha256=")]
    [InlineData(WebhookSignatureScheme.Linear, "Linear-Signature", null)]
    [InlineData(WebhookSignatureScheme.Dropbox, "X-Dropbox-Signature", null)]
    [InlineData(WebhookSignatureScheme.Generic, "X-Signature", "sha256=")]
    public void Raw_body_hex_schemes_validate_round_trip(
        WebhookSignatureScheme scheme,
        string headerName,
        string? prefix)
    {
        // Arrange
        var spec = PartnerSchemeRegistry.TryGet(scheme)!;
        const string key = "k";
        var body = Encoding.UTF8.GetBytes("payload-" + scheme);
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, key, body);
        var headerValue = (prefix ?? string.Empty) + SignatureTestHelper.ToHex(digest);
        var ctx = SignatureTestHelper.Context(spec, body,
            new Dictionary<string, string> { [headerName] = headerValue }, key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.Equal(headerName, spec.HeaderName);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Zoom_validates_v0_colon_delimited_with_timestamp_header()
    {
        // Arrange
        var spec = PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.Zoom)!;
        const string key = "zoom-secret";
        const string ts = "1700000000";
        var body = Encoding.UTF8.GetBytes("{\"event\":\"webhook.test\"}");
        var signed = Encoding.UTF8.GetBytes("v0:" + ts + ":").Concat(body).ToArray();
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, key, signed);
        var ctx = SignatureTestHelper.Context(spec, body, new Dictionary<string, string>
        {
            ["X-Zm-Signature"] = "v0=" + SignatureTestHelper.ToHex(digest),
            ["X-Zm-Request-Timestamp"] = ts,
        }, key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Calendly_validates_t_v1_pair()
    {
        // Arrange
        var spec = PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.Calendly)!;
        const string key = "calendly-secret";
        const string ts = "1700000000";
        var body = Encoding.UTF8.GetBytes("{\"event\":\"invitee.created\"}");
        var signed = Encoding.UTF8.GetBytes(ts + ".").Concat(body).ToArray();
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, key, signed);
        var ctx = SignatureTestHelper.Context(spec, body, new Dictionary<string, string>
        {
            ["Calendly-Webhook-Signature"] = $"t={ts},v1={SignatureTestHelper.ToHex(digest)}",
        }, key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid);
    }
}
