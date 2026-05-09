using System.Text;
using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.UnitTests.Signature;

/// <summary>Vector tests for Microsoft Teams outgoing webhook (<c>Authorization: HMAC &lt;base64&gt;</c>).</summary>
public sealed class MicrosoftTeamsVectorTests
{
    private static readonly WebhookSignatureSpec _spec =
        PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.MicrosoftTeams)!;

    [Fact]
    public void Verify_succeeds_with_HMAC_prefix_in_authorization_header()
    {
        // Arrange
        const string key = "test-key-from-teams-portal";
        var body = Encoding.UTF8.GetBytes("{\"text\":\"hello\"}");
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, key, body);
        var ctx = SignatureTestHelper.Context(_spec, body,
            new Dictionary<string, string> { ["Authorization"] = "HMAC " + SignatureTestHelper.ToBase64(digest) },
            key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Verify_rejects_when_HMAC_prefix_missing()
    {
        // Arrange
        const string key = "k";
        var body = Encoding.UTF8.GetBytes("payload");
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, key, body);
        var ctx = SignatureTestHelper.Context(_spec, body,
            new Dictionary<string, string> { ["Authorization"] = SignatureTestHelper.ToBase64(digest) },
            key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(WebhookSignatureFailureReason.MalformedHeader, result.Reason);
    }
}
