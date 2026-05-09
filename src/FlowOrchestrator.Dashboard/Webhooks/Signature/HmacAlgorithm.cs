namespace FlowOrchestrator.Dashboard.Webhooks.Signature;

/// <summary>
/// Cryptographic hash function backing an HMAC signature scheme. The list maps to
/// the algorithms publishers ship in production webhooks today; it does not include
/// asymmetric (RSA / ECDSA / ED25519) signing — those live behind a different
/// verifier interface.
/// </summary>
public enum HmacAlgorithm
{
    /// <summary>HMAC-SHA1. Disabled by default; opt in via <c>AllowLegacySha1</c>.</summary>
    Sha1,

    /// <summary>HMAC-SHA256. Default for the modern partner schemes.</summary>
    Sha256,

    /// <summary>HMAC-SHA384.</summary>
    Sha384,

    /// <summary>HMAC-SHA512.</summary>
    Sha512,
}
