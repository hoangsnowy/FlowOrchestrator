using System.Text;
using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.UnitTests.Signature;

/// <summary>Vector tests for the GitHub <c>X-Hub-Signature-256</c> dialect.</summary>
public sealed class GitHubVectorTests
{
    private static readonly WebhookSignatureSpec _spec =
        PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.GitHub)!;

    [Fact]
    public void Verify_succeeds_for_matching_signature()
    {
        // Arrange
        const string key = "It's a Secret to Everybody";
        var body = Encoding.UTF8.GetBytes("Hello, World!");
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, key, body);
        var ctx = SignatureTestHelper.Context(_spec, body,
            new Dictionary<string, string> { ["X-Hub-Signature-256"] = "sha256=" + SignatureTestHelper.ToHex(digest) },
            key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid);
        Assert.False(result.UsedRotationKey);
        Assert.Equal(WebhookSignatureFailureReason.None, result.Reason);
    }

    [Fact]
    public void Verify_rejects_when_prefix_missing()
    {
        // Arrange
        const string key = "secret";
        var body = Encoding.UTF8.GetBytes("payload");
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, key, body);
        var ctx = SignatureTestHelper.Context(_spec, body,
            new Dictionary<string, string> { ["X-Hub-Signature-256"] = SignatureTestHelper.ToHex(digest) },
            key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(WebhookSignatureFailureReason.MalformedHeader, result.Reason);
    }

    [Fact]
    public void Verify_rejects_when_header_missing()
    {
        // Arrange
        var verifier = new HmacSignatureVerifier();
        var ctx = SignatureTestHelper.Context(_spec, Encoding.UTF8.GetBytes("payload"),
            new Dictionary<string, string>(), "secret");

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(WebhookSignatureFailureReason.MissingHeader, result.Reason);
    }

    [Fact]
    public void Verify_rejects_digest_mismatch_with_distinct_reason()
    {
        // Arrange
        var ctx = SignatureTestHelper.Context(_spec,
            Encoding.UTF8.GetBytes("payload"),
            new Dictionary<string, string>
            {
                ["X-Hub-Signature-256"] =
                    "sha256=0000000000000000000000000000000000000000000000000000000000000000",
            },
            "secret");
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(WebhookSignatureFailureReason.DigestMismatch, result.Reason);
    }

    [Fact]
    public void Verify_accepts_with_rotated_previous_key()
    {
        // Arrange
        const string previous = "old-secret";
        const string current = "new-secret";
        var body = Encoding.UTF8.GetBytes("rotation test");
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, previous, body);
        var ctx = SignatureTestHelper.Context(_spec, body,
            new Dictionary<string, string> { ["X-Hub-Signature-256"] = "sha256=" + SignatureTestHelper.ToHex(digest) },
            current, previous);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid);
        Assert.True(result.UsedRotationKey);
    }
}
