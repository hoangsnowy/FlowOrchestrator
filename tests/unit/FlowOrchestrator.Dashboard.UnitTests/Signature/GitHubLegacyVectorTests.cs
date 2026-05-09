using System.Text;
using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.UnitTests.Signature;

/// <summary>Vector tests for the legacy GitHub <c>X-Hub-Signature</c> (SHA-1) dialect.</summary>
public sealed class GitHubLegacyVectorTests
{
    private static readonly WebhookSignatureSpec _spec =
        PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.GitHubLegacy)!;

    [Fact]
    public void Verify_rejects_sha1_when_legacy_flag_off()
    {
        // Arrange
        const string key = "k";
        var body = Encoding.UTF8.GetBytes("body");
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha1, key, body);
        var ctx = SignatureTestHelper.Context(_spec, body,
            new Dictionary<string, string> { ["X-Hub-Signature"] = "sha1=" + SignatureTestHelper.ToHex(digest) },
            key, allowLegacySha1: false);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(WebhookSignatureFailureReason.AlgorithmNotAllowed, result.Reason);
    }

    [Fact]
    public void Verify_accepts_sha1_when_legacy_flag_on()
    {
        // Arrange
        const string key = "k";
        var body = Encoding.UTF8.GetBytes("body");
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha1, key, body);
        var ctx = SignatureTestHelper.Context(_spec, body,
            new Dictionary<string, string> { ["X-Hub-Signature"] = "sha1=" + SignatureTestHelper.ToHex(digest) },
            key, allowLegacySha1: true);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid);
    }
}
