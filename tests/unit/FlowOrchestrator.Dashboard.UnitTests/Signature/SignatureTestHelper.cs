using System.Security.Cryptography;
using System.Text;
using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.UnitTests.Signature;

/// <summary>
/// Test-only helpers for materialising expected signatures and contexts that
/// match production publisher dialects. Mirrors the formats produced by GitHub,
/// Stripe, Slack and so on so each vector test can assert against a known good
/// digest computed locally.
/// </summary>
internal static class SignatureTestHelper
{
    public static byte[] Hmac(HmacAlgorithm algo, string key, byte[] payload)
    {
        var k = Encoding.UTF8.GetBytes(key);
        return algo switch
        {
            HmacAlgorithm.Sha1 => HMACSHA1.HashData(k, payload),
            HmacAlgorithm.Sha256 => HMACSHA256.HashData(k, payload),
            HmacAlgorithm.Sha384 => HMACSHA384.HashData(k, payload),
            HmacAlgorithm.Sha512 => HMACSHA512.HashData(k, payload),
            _ => throw new InvalidOperationException(),
        };
    }

    public static string ToHex(byte[] bytes, bool upper = false) =>
        upper ? Convert.ToHexString(bytes) : Convert.ToHexString(bytes).ToLowerInvariant();

    public static string ToBase64(byte[] bytes) => Convert.ToBase64String(bytes);

    public static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static WebhookSignatureContext Context(
        WebhookSignatureSpec spec,
        byte[] body,
        IDictionary<string, string> headers,
        string key,
        string? previous = null,
        string? absoluteUrl = null,
        IDictionary<string, string>? formFields = null,
        bool allowLegacySha1 = false) =>
        new()
        {
            Body = body,
            Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
            AbsoluteUrl = absoluteUrl,
            FormFields = formFields is null ? null : new Dictionary<string, string>(formFields, StringComparer.OrdinalIgnoreCase),
            Spec = spec,
            HmacKey = key,
            HmacKeyPrevious = previous,
            AllowLegacySha1 = allowLegacySha1,
        };
}
