using System.Text;
using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.UnitTests.Signature;

/// <summary>
/// Pins which values <see cref="HmacSignatureVerifier"/> reports as authenticated: only those the verified
/// digest actually covered, so replay protection never trusts an unsigned header.
/// </summary>
public sealed class AuthenticatedMaterialTests
{
    private const string Key = "whsec_test";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"id\":\"evt_1\"}");

    [Fact]
    public void Stripe_reports_the_signed_t_value_as_the_authenticated_timestamp()
    {
        // Arrange
        var spec = PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.Stripe)!;
        const string ts = "1700000000";
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, Key, Encoding.UTF8.GetBytes(ts + "." + Encoding.UTF8.GetString(Body)));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Stripe-Signature"] = $"t={ts},v1={SignatureTestHelper.ToHex(digest)}",
            ["X-Webhook-Timestamp"] = "1800000000",
        };

        // Act
        var result = new HmacSignatureVerifier().Verify(SignatureTestHelper.Context(spec, Body, headers, Key));

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(ts, result.AuthenticatedTimestamp);
        Assert.Null(result.AuthenticatedNonce);
    }

    [Fact]
    public void Body_only_scheme_reports_no_authenticated_timestamp()
    {
        // Arrange
        var spec = PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.GitHub)!;
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, Key, Body);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Hub-Signature-256"] = "sha256=" + SignatureTestHelper.ToHex(digest),
            ["X-Webhook-Timestamp"] = "1700000000",
        };

        // Act
        var result = new HmacSignatureVerifier().Verify(SignatureTestHelper.Context(spec, Body, headers, Key));

        // Assert
        Assert.True(result.IsValid);
        Assert.Null(result.AuthenticatedTimestamp);
        Assert.Null(result.AuthenticatedNonce);
    }

    [Fact]
    public void Mailgun_reports_the_signed_timestamp_and_token()
    {
        // Arrange
        var spec = PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.Mailgun)!;
        const string ts = "1700000000";
        const string token = "abcdef0123456789";
        var hex = SignatureTestHelper.ToHex(SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, Key, Encoding.UTF8.GetBytes(ts + token)));
        var body = Encoding.UTF8.GetBytes($"{{\"signature\":{{\"timestamp\":\"{ts}\",\"token\":\"{token}\",\"signature\":\"{hex}\"}}}}");

        // Act
        var result = new HmacSignatureVerifier().Verify(
            SignatureTestHelper.Context(spec, body, new Dictionary<string, string>(), Key));

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(ts, result.AuthenticatedTimestamp);
        Assert.Equal(token, result.AuthenticatedNonce);
    }
}
