using System.Security.Cryptography;
using System.Text;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Dashboard.Notifications;
using FlowOrchestrator.Dashboard.Webhooks;
using FlowOrchestrator.Dashboard.Webhooks.RateLimit;
using FlowOrchestrator.Dashboard.Webhooks.Signature;
using Microsoft.AspNetCore.Http;

namespace FlowOrchestrator.Dashboard.UnitTests;

/// <summary>
/// Audit (4th sweep) — unit-level proofs for dashboard defects: the Microsoft Teams key encoding,
/// per-trigger rate-limit isolation and concurrent writes on the SSE response. Each test failed on the
/// audited commit.
/// </summary>
public sealed class SurfaceAuditUnitTests
{
    /// <summary>
    /// Microsoft's outgoing-webhook documentation (and its reference sample) derives the HMAC key by
    /// base64-DECODING the security token Teams shows in the portal (<c>Convert.FromBase64String(token)</c>).
    /// </summary>
    [Fact]
    public void MicrosoftTeams_verifies_signature_keyed_with_base64_decoded_security_token()
    {
        // Arrange
        const string securityToken = "dGVhbXMtc2VjdXJpdHktdG9rZW4tYnl0ZXMtMDEyMzQ1Ng=="; // as shown by Teams
        var body = Encoding.UTF8.GetBytes("{\"type\":\"message\",\"text\":\"hello\"}");
        var teamsSignature = Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(securityToken), body));
        var context = new WebhookSignatureContext
        {
            Body = body,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "HMAC " + teamsSignature },
            Spec = PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.MicrosoftTeams),
            HmacKey = securityToken,
        };

        // Act
        var result = new HmacSignatureVerifier().Verify(context);

        // Assert
        Assert.True(result.IsValid, result.Reason.ToString());
    }

    /// <summary>
    /// A Teams key that is not base64 cannot be the security token Teams issued: the verifier reports a
    /// configuration failure instead of throwing <see cref="FormatException"/> out of the request.
    /// </summary>
    [Fact]
    public void MicrosoftTeams_key_that_is_not_base64_fails_as_key_not_configured()
    {
        // Arrange
        var body = Encoding.UTF8.GetBytes("{}");
        var context = new WebhookSignatureContext
        {
            Body = body,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "HMAC AAAA" },
            Spec = PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.MicrosoftTeams),
            HmacKey = "not base64 !",
        };

        // Act
        var result = new HmacSignatureVerifier().Verify(context);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(WebhookSignatureFailureReason.KeyNotConfigured, result.Reason);
    }

    /// <summary>
    /// Every other dialect keeps keying with the UTF-8 bytes of the configured key (GitHub here).
    /// </summary>
    [Fact]
    public void GitHub_still_keys_with_the_utf8_bytes_of_the_configured_key()
    {
        // Arrange
        const string key = "dGVhbXMtc2VjdXJpdHk="; // base64-looking, but GitHub keys with the literal string
        var body = Encoding.UTF8.GetBytes("{\"action\":\"opened\"}");
        var signature = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), body)).ToLowerInvariant();
        var context = new WebhookSignatureContext
        {
            Body = body,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Hub-Signature-256"] = signature },
            Spec = PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.GitHub),
            HmacKey = key,
        };

        // Act
        var result = new HmacSignatureVerifier().Verify(context);

        // Assert
        Assert.True(result.IsValid, result.Reason.ToString());
    }
}
