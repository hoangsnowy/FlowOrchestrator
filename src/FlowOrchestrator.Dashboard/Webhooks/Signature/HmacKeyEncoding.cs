namespace FlowOrchestrator.Dashboard.Webhooks.Signature;

/// <summary>
/// How the configured HMAC key string (<c>webhookHmacKey</c> / <c>webhookHmacKeyPrevious</c>) is turned
/// into the bytes the digest is keyed with.
/// </summary>
public enum HmacKeyEncoding
{
    /// <summary>The UTF-8 bytes of the key string — what almost every publisher does.</summary>
    Utf8,

    /// <summary>
    /// The key string is base64 and the HMAC is keyed with the decoded bytes (Microsoft Teams outgoing
    /// webhooks: the security token Teams shows at registration is base64; Standard Webhooks: the secret is
    /// <c>whsec_</c> followed by base64).
    /// </summary>
    /// <remarks>
    /// A leading <c>whsec_</c> is stripped before decoding, so a Standard Webhooks secret can be pasted exactly
    /// as the publisher's console shows it. The prefix is unambiguous: <c>_</c> is not in the base64 alphabet, so
    /// no valid base64 key can start with it. A key that is not valid base64 (after the optional prefix) or decodes
    /// to zero bytes fails verification with <see cref="WebhookSignatureFailureReason.KeyNotConfigured"/>.
    /// </remarks>
    Base64,
}
