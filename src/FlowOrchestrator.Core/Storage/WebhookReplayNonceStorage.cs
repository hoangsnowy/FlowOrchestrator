using System.Security.Cryptography;
using System.Text;

namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// The one bound every built-in <see cref="IWebhookReplayStore"/> applies to a replay nonce before it is
/// stored or looked up, so SQL Server, PostgreSQL and the in-memory store register the same key.
/// </summary>
/// <remarks>
/// On the unsigned path the nonce is the raw value of a caller-chosen header (e.g. <c>X-Delivery-Id</c>) and
/// nothing upstream caps its length, while the SQL Server / PostgreSQL nonce columns hold
/// <see cref="MaxStoredLength"/> characters. A nonce that would not fit is stored as <see cref="DigestPrefix"/> +
/// the lower-case hex SHA-256 of its UTF-8 bytes (71 characters): the same long nonce still maps to the same
/// row, so it keeps its replay protection, and no schema change is needed. Nonces of
/// <see cref="MaxStoredLength"/> characters or fewer are stored exactly as before (no trimming, no case
/// folding — a nonce is an opaque token). <c>ReplayProtectionGate</c> applies it before calling the store,
/// so custom stores receive bounded nonces; the built-in stores apply it again for direct callers.
/// </remarks>
public static class WebhookReplayNonceStorage
{
    /// <summary>Length of the nonce columns of the built-in SQL Server and PostgreSQL schemas.</summary>
    public const int MaxStoredLength = 256;

    /// <summary>Prefix marking a stored nonce as the digest of a longer one.</summary>
    public const string DigestPrefix = "sha256:";

    /// <summary>
    /// Returns <paramref name="nonce"/> unchanged when it fits <see cref="MaxStoredLength"/>, and its digest
    /// form otherwise.
    /// </summary>
    /// <param name="nonce">The nonce the replay gate derived for a delivery.</param>
    /// <returns>A string of at most <see cref="MaxStoredLength"/> characters; idempotent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="nonce"/> is <see langword="null"/>.</exception>
    public static string ToStorageForm(string nonce)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        return nonce.Length <= MaxStoredLength ? nonce : Digest(nonce);
    }

    private static string Digest(string nonce)
    {
        // Only for nonces over 256 characters, once per delivery — not a hot path.
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(nonce), hash);
#if NET9_0_OR_GREATER
        return string.Concat(DigestPrefix, Convert.ToHexStringLower(hash));
#else
        return string.Concat(DigestPrefix, Convert.ToHexString(hash).ToLowerInvariant());
#endif
    }
}
