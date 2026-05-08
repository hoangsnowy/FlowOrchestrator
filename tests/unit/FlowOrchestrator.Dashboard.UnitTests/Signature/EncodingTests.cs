using System.Text;
using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.UnitTests.Signature;

/// <summary>Encoding edge cases: hex case insensitivity, base64 padding, base64url variants.</summary>
public sealed class EncodingTests
{
    [Theory]
    [InlineData(SignatureEncoding.HexLower)]
    [InlineData(SignatureEncoding.HexUpper)]
    public void Hex_decode_is_case_insensitive(SignatureEncoding encoding)
    {
        // Arrange
        const string key = "k";
        var body = Encoding.UTF8.GetBytes("p");
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, key, body);
        var hex = encoding == SignatureEncoding.HexUpper
            ? SignatureTestHelper.ToHex(digest, upper: true)
            : SignatureTestHelper.ToHex(digest);
        var spec = new WebhookSignatureSpec
        {
            HeaderName = "X-Sig",
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = encoding,
            SignedPayloadStrategy = SignedPayloadStrategy.RawBody,
        };
        var ctx = SignatureTestHelper.Context(spec, body,
            new Dictionary<string, string> { ["X-Sig"] = hex }, key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Base64Url_decode_tolerates_missing_padding()
    {
        // Arrange
        const string key = "k";
        var body = Encoding.UTF8.GetBytes("hello");
        var digest = SignatureTestHelper.Hmac(HmacAlgorithm.Sha256, key, body);
        var b64url = SignatureTestHelper.ToBase64Url(digest); // padding stripped
        var spec = new WebhookSignatureSpec
        {
            HeaderName = "X-Sig",
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = SignatureEncoding.Base64Url,
            SignedPayloadStrategy = SignedPayloadStrategy.RawBody,
        };
        var ctx = SignatureTestHelper.Context(spec, body,
            new Dictionary<string, string> { ["X-Sig"] = b64url }, key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid);
    }
}
