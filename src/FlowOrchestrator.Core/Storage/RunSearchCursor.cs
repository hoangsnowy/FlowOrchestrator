using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;

namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Keyset position in the run list: the <c>(StartedAt, Id)</c> of the last row of a page. Passing it
/// back as <see cref="RunSearchQuery.After"/> reads the rows that follow it, newest first, with an index
/// seek instead of an <c>OFFSET</c> that re-reads every skipped row.
/// </summary>
/// <param name="StartedAt">Start time of the last row of the previous page.</param>
/// <param name="Id">Run id of the last row of the previous page; breaks ties between equal start times.</param>
/// <remarks>
/// <para>The run list is ordered by <c>StartedAt</c> descending, then <c>Id</c> ascending in the provider's
/// own key order (SQL Server orders <c>uniqueidentifier</c> differently from .NET and PostgreSQL). A
/// cursor is therefore only meaningful to the provider that issued it; treat the encoded form as opaque.</para>
/// <para>Unlike <see cref="RunSearchQuery.Skip"/>, a cursor is stable while new runs are inserted at
/// the head of the list: the next page starts after the same row instead of shifting by the number of
/// runs started in between.</para>
/// </remarks>
public readonly record struct RunSearchCursor(DateTimeOffset StartedAt, Guid Id)
{
    // Wire format, version 1: [0x01][UtcTicks: int64 big-endian][Guid: 16 bytes] = 25 bytes, base64url (34 chars).
    private const byte FormatVersion = 1;
    private const int EncodedByteLength = 1 + sizeof(long) + 16;

    /// <summary>Upper bound on the length of an encoded cursor; longer input is rejected without decoding.</summary>
    public const int MaxEncodedLength = 64;

    /// <summary>
    /// Sorts runs into list order with .NET <see cref="Guid"/> ordering for the tie-break: the order
    /// <see cref="Precedes"/> pages by. Used by the default <see cref="IFlowRunStore.SearchRunsAsync"/>.
    /// </summary>
    internal static readonly IComparer<FlowRunRecord> ListOrder = Comparer<FlowRunRecord>.Create(
        static (a, b) => a.StartedAt != b.StartedAt ? b.StartedAt.CompareTo(a.StartedAt) : a.Id.CompareTo(b.Id));

    /// <summary>Creates the cursor that continues after <paramref name="run"/>.</summary>
    /// <param name="run">The last row of a page.</param>
    /// <returns>The keyset position of that row.</returns>
    public static RunSearchCursor After(FlowRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new RunSearchCursor(run.StartedAt, run.Id);
    }

    /// <summary>
    /// Encodes the cursor as a URL-safe token (base64url, no padding) for an HTTP query string.
    /// </summary>
    /// <returns>The opaque token; decode it with <see cref="TryParse"/>.</returns>
    /// <remarks>The start time is stored as UTC ticks, so the offset of the original value is not preserved — the instant is.</remarks>
    public string Encode()
    {
        Span<byte> bytes = stackalloc byte[EncodedByteLength];
        bytes[0] = FormatVersion;
        BinaryPrimitives.WriteInt64BigEndian(bytes[1..], StartedAt.UtcTicks);
        Id.TryWriteBytes(bytes[(1 + sizeof(long))..], bigEndian: true, out _);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>Decodes a token produced by <see cref="Encode"/>.</summary>
    /// <param name="value">The token, typically from the <c>after</c> query parameter.</param>
    /// <param name="cursor">The decoded cursor when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> for blank, over-long, malformed or unknown-version input; never throws.</returns>
    public static bool TryParse(ReadOnlySpan<char> value, out RunSearchCursor cursor)
    {
        cursor = default;
        value = value.Trim();
        if (value.IsEmpty || value.Length > MaxEncodedLength)
            return false;

        // IsValid first: the decoder throws on characters outside the base64url alphabet.
        if (!Base64Url.IsValid(value, out var decodedLength) || decodedLength != EncodedByteLength)
            return false;

        Span<byte> bytes = stackalloc byte[EncodedByteLength];
        if (Base64Url.DecodeFromChars(value, bytes, out _, out var written) != OperationStatus.Done
            || written != EncodedByteLength
            || bytes[0] != FormatVersion)
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes[1..]);
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            return false;

        var id = new Guid(bytes.Slice(1 + sizeof(long), 16), bigEndian: true);
        cursor = new RunSearchCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="run"/> sorts strictly after this cursor in the
    /// in-process order (<c>StartedAt</c> descending, then <see cref="Guid.CompareTo(Guid)"/> ascending).
    /// </summary>
    /// <param name="run">The candidate row.</param>
    /// <returns>Whether the row belongs to a page that continues after the cursor.</returns>
    /// <remarks>Used by in-process providers; SQL providers express the same predicate in their own key order.</remarks>
    public bool Precedes(FlowRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var byTime = run.StartedAt.CompareTo(StartedAt);
        return byTime < 0 || (byTime == 0 && run.Id.CompareTo(Id) > 0);
    }
}
