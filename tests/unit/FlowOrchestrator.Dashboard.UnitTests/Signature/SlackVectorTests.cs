using System.Text;
using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.UnitTests.Signature;

/// <summary>Vector tests for the Slack <c>X-Slack-Signature</c> dialect (<c>v0:ts:body</c>).</summary>
public sealed class SlackVectorTests
{
    private static readonly WebhookSignatureSpec _spec =
        PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.Slack)!;

    [Fact]
    public void Verify_succeeds_with_v0_ts_body_payload()
    {
        // Arrange
        const string key = "8f742231b10e8888abcd99yyyzzz85a5";
        const string ts = "1531420618";
        var body = Encoding.UTF8.GetBytes(
            "token=xyzz0WbapA4vBCDEFasx0q6G&team_id=T1DC2JH3J&team_domain=testteamnow");
        var signed = Encoding.UTF8.GetBytes("v0:" + ts + ":").Concat(body).ToArray();
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, key, signed);
        var ctx = SignatureTestHelper.Context(_spec, body, new Dictionary<string, string>
        {
            ["X-Slack-Signature"] = "v0=" + SignatureTestHelper.ToHex(digest),
            ["X-Slack-Request-Timestamp"] = ts,
        }, key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Verify_rejects_when_timestamp_header_missing()
    {
        // Arrange
        const string key = "k";
        var body = Encoding.UTF8.GetBytes("payload");
        var ctx = SignatureTestHelper.Context(_spec, body, new Dictionary<string, string>
        {
            ["X-Slack-Signature"] = "v0=deadbeef",
        }, key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(WebhookSignatureFailureReason.MissingTimestamp, result.Reason);
    }
}
