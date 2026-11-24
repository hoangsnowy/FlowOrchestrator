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
    /// webhooks: the security token Teams shows at registration is base64).
    /// </summary>
    Base64,
}
