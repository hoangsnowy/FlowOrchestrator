using System.Text;
using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.UnitTests.Signature;

/// <summary>Tests the user-driven Custom scheme path including custom payload strategies.</summary>
public sealed class CustomSchemeTests
{
    [Fact]
    public void Custom_strategy_is_invoked_when_named()
    {
        // Arrange
        const string key = "k";
        var body = Encoding.UTF8.GetBytes("body");
        // Custom strategy: HMAC over "PREFIX|" + body
        byte[] BuildPayload(WebhookSignatureContext c)
        {
            var prefix = Encoding.UTF8.GetBytes("PREFIX|");
            var output = new byte[prefix.Length + c.Body.Length];
            Buffer.BlockCopy(prefix, 0, output, 0, prefix.Length);
            c.Body.Span.CopyTo(output.AsSpan(prefix.Length));
            return output;
        }
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, key,
            Encoding.UTF8.GetBytes("PREFIX|").Concat(body).ToArray());
        var spec = new WebhookSignatureSpec
        {
            HeaderName = "X-Custom",
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = SignatureEncoding.HexLower,
            SignedPayloadStrategy = SignedPayloadStrategy.Custom,
            CustomStrategyName = "prefix-pipe",
        };
        var ctx = SignatureTestHelper.Context(spec, body,
            new Dictionary<string, string> { ["X-Custom"] = SignatureTestHelper.ToHex(digest) }, key);
        var verifier = new HmacSignatureVerifier(new Dictionary<string, Func<WebhookSignatureContext, byte[]>>
        {
            ["prefix-pipe"] = BuildPayload,
        });

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid);
    }
}
