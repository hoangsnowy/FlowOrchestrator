using System.Security.Cryptography;
using System.Text;

namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// The one normalisation every built-in store applies to a caller-supplied idempotency key before it is
/// stored or looked up, so SQL Server, PostgreSQL and the in-memory store deduplicate identically.
/// </summary>
/// <remarks>
/// The key is the raw value of the client's <c>Idempotency-Key</c> header and nothing upstream caps its
/// length, while the SQL Server / PostgreSQL key columns hold <see cref="MaxStoredLength"/> characters.
/// A key that would not fit is stored as <see cref="DigestPrefix"/> + the lower-case hex SHA-256 of its
/// normalised UTF-8 bytes (71 characters): equal keys still map to the same row, and no schema change is
/// needed. Custom stores should call <see cref="Normalize"/> too, so a key deduplicates the same way
/// whichever backend a deployment moves to.
/// </remarks>
public static class IdempotencyKeyStorage
{
    /// <summary>Length of the idempotency-key columns of the built-in SQL Server and PostgreSQL schemas.</summary>
    public const int MaxStoredLength = 256;

    /// <summary>Prefix marking a stored key as the digest of a longer one.</summary>
    public const string DigestPrefix = "sha256:";

    /// <summary>
    /// Returns the deduplication form of <paramref name="idempotencyKey"/>: trimmed, lower-cased with the
    /// invariant culture, and replaced by its digest when that is longer than <see cref="MaxStoredLength"/>.
    /// </summary>
    /// <param name="idempotencyKey">The caller-supplied key.</param>
    /// <returns>A string of at most <see cref="MaxStoredLength"/> characters.</returns>
    /// <remarks>
    /// Trimming and lower-casing match what every built-in store already did before the length cap was
    /// added, so keys of 256 characters or fewer are stored exactly as before.
    /// </remarks>
    public static string Normalize(string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        var normalized = idempotencyKey.Trim().ToLowerInvariant();
        return normalized.Length <= MaxStoredLength ? normalized : Digest(normalized);
    }

    /// <summary>
    /// Returns <paramref name="idempotencyKey"/> unchanged when it fits <see cref="MaxStoredLength"/>, and its
    /// <see cref="Normalize"/>d digest form otherwise.
    /// </summary>
    /// <param name="idempotencyKey">The caller-supplied key, or <see langword="null"/>.</param>
    /// <returns>A value that fits the run-control record's idempotency-key column.</returns>
    /// <remarks>
    /// For the informational copy on <see cref="FlowRunControlRecord.IdempotencyKey"/>: a short key keeps
    /// the caller's spelling there, a long one is recorded as the same digest the dedup table holds.
    /// </remarks>
    public static string? ToStorageForm(string? idempotencyKey)
        => idempotencyKey is null || idempotencyKey.Length <= MaxStoredLength
            ? idempotencyKey
            : Normalize(idempotencyKey);

    /// <summary>
    /// Returns the cancel reason a store records on a run whose idempotency reservation was taken over by
    /// <see cref="IFlowRunControlStore.TryTakeOverStaleIdempotencyKeyAsync"/>.
    /// </summary>
    /// <param name="newRunId">The run that took the key over.</param>
    /// <returns>A reason naming <paramref name="newRunId"/>, so the dashboard links the dead run to its replacement.</returns>
    public static string TakenOverCancelReason(Guid newRunId)
        => $"Idempotency key taken over by run {newRunId}: this run's trigger stopped before it started any step.";

    private static string Digest(string normalized)
    {
        // Runs only for keys over 256 characters, once per trigger — not a hot path.
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(normalized), hash);
        return string.Concat(DigestPrefix, Convert.ToHexStringLower(hash));
    }
}
