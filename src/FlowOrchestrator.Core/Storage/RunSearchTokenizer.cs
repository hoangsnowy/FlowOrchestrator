using System.Buffers;
using System.Text;
using System.Text.Json;

namespace FlowOrchestrator.Core.Storage;

/// <summary>Where a search token came from; stored with the token so a search can be limited to some sources.</summary>
public enum RunSearchTokenSource : byte
{
    /// <summary>Run columns: id, flow name, trigger key, background job id.</summary>
    Run = 1,

    /// <summary>Single-token string values of the trigger body.</summary>
    Trigger = 2,

    /// <summary>Step keys.</summary>
    StepKey = 3,

    /// <summary>Words of step error messages.</summary>
    Error = 4,

    /// <summary>Single-token string values of step output JSON.</summary>
    Payload = 5,
}

/// <summary>
/// Tokenizer for the portable run-search token index (<c>FlowRunSearchTokens</c>): turns run, step and
/// payload text into lower-cased identifier tokens, and a search term into the tokens to probe.
/// </summary>
/// <remarks>
/// <para>A token is a run of letters, digits and the identifier joiners <c>- _ . @ :</c>, so
/// <c>ORD-2026-0001234</c> and <c>jane@example.com</c> stay one token. The joiner-separated parts are
/// emitted too (<c>ord</c>, <c>2026</c>, <c>0001234</c>), and for names the camelCase humps
/// (<c>InvoiceSyncFlow</c> → <c>invoice</c>, <c>sync</c>, <c>flow</c>), so a fragment is still a token
/// prefix.</para>
/// <para>From JSON only <b>scalar string values without whitespace</b> are indexed — ids, codes, emails,
/// statuses — and array elements are skipped. Prose inside values is deliberately not indexed: that is
/// a full-text engine's job, which an <see cref="IRunSearchIndex"/> implementation can plug in.</para>
/// <para>Cost: O(length of the input) per document, one string per distinct token. It runs on the step
/// completion path only when the token index is enabled.</para>
/// </remarks>
public static class RunSearchTokenizer
{
    /// <summary>Shortest indexed token.</summary>
    public const int MinTokenLength = 2;

    /// <summary>Longest indexed token; longer composites are dropped, their parts are still indexed.</summary>
    public const int MaxTokenLength = 64;

    /// <summary>Default cap on distinct tokens taken from one document.</summary>
    public const int DefaultMaxTokensPerDocument = 256;

    private const string Joiners = "-_.@:";

    /// <summary>Adds the tokens of free text (names, keys, error messages) to <paramref name="sink"/>.</summary>
    /// <param name="text">The text; <see langword="null"/> or empty adds nothing.</param>
    /// <param name="sink">Receives lower-cased tokens. Use an ordinal comparer.</param>
    /// <param name="camelCase">Also split camelCase humps (for flow names and step keys).</param>
    /// <param name="maxTokens">Stops once <paramref name="sink"/> holds this many tokens.</param>
    public static void AddText(ReadOnlySpan<char> text, HashSet<string> sink, bool camelCase = false, int maxTokens = DefaultMaxTokensPerDocument)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var i = 0;
        while (i < text.Length && sink.Count < maxTokens)
        {
            while (i < text.Length && !IsTokenChar(text[i])) i++;
            var start = i;
            while (i < text.Length && IsTokenChar(text[i])) i++;
            if (i > start) AddComposite(text[start..i], sink, camelCase, maxTokens);
        }
    }

    /// <summary>
    /// Adds the single-token scalar string values of a JSON document to <paramref name="sink"/>.
    /// Invalid JSON adds nothing and never throws.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="sink">Receives lower-cased tokens. Use an ordinal comparer.</param>
    /// <param name="maxTokens">Stops once <paramref name="sink"/> holds this many tokens.</param>
    public static void AddJsonValues(string? json, HashSet<string> sink, int maxTokens = DefaultMaxTokensPerDocument)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (string.IsNullOrEmpty(json))
            return;

        var rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(json.Length));
        try
        {
            var length = Encoding.UTF8.GetBytes(json, rented);
            AddJsonValues(rented.AsSpan(0, length), sink, maxTokens);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void AddJsonValues(ReadOnlySpan<byte> utf8, HashSet<string> sink, int maxTokens)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Span<char> buffer = stackalloc char[MaxTokenLength];
        try
        {
            var arrayDepth = 0;
            while (sink.Count < maxTokens && reader.Read())
            {
                // Array elements (line items, histories) multiply the token count by the array length,
                // while the identifiers people search for are scalar properties of the document.
                if (reader.TokenType == JsonTokenType.StartArray) { arrayDepth++; continue; }
                if (reader.TokenType == JsonTokenType.EndArray) { arrayDepth--; continue; }
                if (arrayDepth > 0 || reader.TokenType != JsonTokenType.String)
                    continue;

                // A value longer than MaxTokenLength chars cannot be one token; skip before decoding.
                var encodedLength = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
                if (encodedLength > MaxTokenLength * 6)
                    continue;

                int written;
                try
                {
                    written = reader.CopyString(buffer);
                }
                catch (ArgumentException)
                {
                    continue;   // unescaped value longer than the buffer
                }

                var value = buffer[..written];
                if (value.Length < MinTokenLength || value.ContainsAny(' ', '\t', '\n') || value.Contains('\r'))
                    continue;
                AddText(value, sink, camelCase: false, maxTokens);
            }
        }
        catch (JsonException)
        {
            // Not JSON (or truncated): keep what was read, never fail the write.
        }
    }

    private static void AddComposite(ReadOnlySpan<char> raw, HashSet<string> sink, bool camelCase, int maxTokens)
    {
        raw = raw.Trim(Joiners);
        if (raw.Length < MinTokenLength)
            return;
        if (raw.Length <= MaxTokenLength)
            AddLower(raw, sink);

        // A GUID is searched by its prefix; its hyphen-separated groups are noise.
        if (raw.Length == 36 && Guid.TryParseExact(raw, "D", out _))
            return;

        // Parts split on joiners, and camelCase humps when asked (InvoiceSyncFlow -> invoice, sync, flow).
        var start = 0;
        for (var i = 0; i <= raw.Length && sink.Count < maxTokens; i++)
        {
            var boundary = i == raw.Length || IsJoiner(raw[i]);
            var hump = camelCase && i > start && i < raw.Length && char.IsUpper(raw[i]) && char.IsLower(raw[i - 1]);
            if (!boundary && !hump)
                continue;
            var part = raw[start..i];
            if (part.Length >= MinTokenLength && part.Length <= MaxTokenLength && part.Length != raw.Length)
                AddLower(part, sink);
            start = boundary ? i + 1 : i;
        }
    }

    private static void AddLower(ReadOnlySpan<char> token, HashSet<string> sink)
    {
        Span<char> lower = stackalloc char[MaxTokenLength];
        var length = token.ToLowerInvariant(lower);
        // Probe with the span first so a repeated token (the common case in payloads) allocates nothing.
        if (sink.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup) && lookup.Contains(lower[..length]))
            return;
        sink.Add(lower[..length].ToString());
    }

    private static bool IsJoiner(char c) => c is '-' or '_' or '.' or '@' or ':';

    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || IsJoiner(c);

    /// <summary>
    /// Returns the tokens of a user search term, longest first (the most selective probe leads).
    /// A search matches runs that have a token starting with every returned query token.
    /// </summary>
    /// <param name="term">The search term.</param>
    /// <param name="maxTokens">The longest <paramref name="maxTokens"/> tokens are returned; the rest are dropped, which widens the match.</param>
    /// <returns>Distinct lower-cased tokens; empty when the term has no token of <see cref="MinTokenLength"/> characters.</returns>
    public static IReadOnlyList<string> QueryTokens(ReadOnlySpan<char> term, int maxTokens = 4)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        while (i < term.Length)
        {
            while (i < term.Length && !IsTokenChar(term[i])) i++;
            var start = i;
            while (i < term.Length && IsTokenChar(term[i])) i++;
            var raw = term[start..i].Trim(Joiners);
            if (raw.Length >= MinTokenLength)
                AddLower(raw[..Math.Min(raw.Length, MaxTokenLength)], set);
        }

        var list = new List<string>(set);
        list.Sort(static (a, b) => b.Length != a.Length ? b.Length.CompareTo(a.Length) : string.CompareOrdinal(a, b));
        if (list.Count > maxTokens)
            list.RemoveRange(maxTokens, list.Count - maxTokens);
        return list;
    }
}
