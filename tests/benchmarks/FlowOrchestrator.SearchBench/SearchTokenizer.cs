using System.Text;
using System.Text.Json;

namespace FlowOrchestrator.SearchBench;

/// <summary>Where a search token came from; stored with the token so quick search can ignore payload tokens.</summary>
public enum TokenSource : byte
{
    /// <summary>Run columns: id, flow name, trigger key, status, background job id.</summary>
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
/// PROTOTYPE tokenizer for the portable inverted index (<c>FlowRunSearchTokens</c>). Lower-cases,
/// keeps identifier joiners (<c>- _ . @ :</c>) inside a token so <c>ORD-2026-0001234</c> stays one
/// token, and additionally emits the joiner-separated parts (and camelCase parts of names) so a
/// fragment such as <c>0001234</c> or <c>sync</c> is still a token prefix.
/// </summary>
public static class SearchTokenizer
{
    public const int MinLength = 2;
    public const int MaxLength = 64;

    /// <summary>Adds the tokens of free text (names, keys, error messages).</summary>
    public static void AddText(string? text, HashSet<string> sink, bool camelCase = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        var span = text.AsSpan();
        var i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && !IsTokenChar(span[i])) i++;
            var start = i;
            while (i < span.Length && IsTokenChar(span[i])) i++;
            if (i > start) AddComposite(span[start..i], sink, camelCase);
        }
    }

    /// <summary>
    /// Adds single-token string values of a JSON document (ids, codes, emails, statuses). Values that
    /// contain whitespace are prose and are skipped: indexing every word of every payload is the
    /// full-text-engine job this prototype is deliberately not doing.
    /// </summary>
    public static void AddJsonValues(string? json, HashSet<string> sink, int maxTokens = int.MaxValue)
    {
        if (string.IsNullOrEmpty(json)) return;
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        try
        {
            var arrayDepth = 0;
            while (reader.Read() && sink.Count < maxTokens)
            {
                // Array elements (line items, histories) are skipped: they multiply the token count by the
                // array length while the identifiers people search for are scalar properties of the document.
                if (reader.TokenType == JsonTokenType.StartArray) { arrayDepth++; continue; }
                if (reader.TokenType == JsonTokenType.EndArray) { arrayDepth--; continue; }
                if (arrayDepth > 0 || reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > MaxLength * 3) continue;
                var value = reader.GetString();
                if (value is null || value.Length < MinLength || value.Length > MaxLength) continue;
                if (value.AsSpan().ContainsAny(' ', '\t', '\n')) continue;
                AddText(value, sink);
            }
        }
        catch (JsonException)
        {
            // Not JSON: index nothing rather than fail the write.
        }
    }

    private static void AddComposite(ReadOnlySpan<char> raw, HashSet<string> sink, bool camelCase)
    {
        raw = raw.Trim("-_.@:");
        if (raw.Length < MinLength) return;
        if (raw.Length <= MaxLength) sink.Add(raw.ToString().ToLowerInvariant());
        // A GUID is searched by its prefix; its hyphen-separated groups are noise.
        if (raw.Length == 36 && Guid.TryParseExact(raw, "D", out _)) return;

        // Parts split on joiners, and camelCase humps for names (InvoiceSyncFlow -> invoice, sync, flow).
        var start = 0;
        for (var i = 0; i <= raw.Length; i++)
        {
            var boundary = i == raw.Length || IsJoiner(raw[i]);
            var hump = camelCase && i > start && i < raw.Length && char.IsUpper(raw[i]) && char.IsLower(raw[i - 1]);
            if (!boundary && !hump) continue;
            var part = raw[start..i];
            if (part.Length >= MinLength && part.Length <= MaxLength && part.Length != raw.Length)
                sink.Add(part.ToString().ToLowerInvariant());
            start = boundary ? i + 1 : i;
        }
    }

    private static bool IsJoiner(char c) => c is '-' or '_' or '.' or '@' or ':';

    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || IsJoiner(c);

    /// <summary>Tokens of a user search term, longest first (the most selective probe leads).</summary>
    public static List<string> QueryTokens(string term)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var span = term.AsSpan();
        var i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && !IsTokenChar(span[i])) i++;
            var start = i;
            while (i < span.Length && IsTokenChar(span[i])) i++;
            var raw = span[start..i].Trim("-_.@:");
            if (raw.Length >= MinLength) set.Add(raw[..Math.Min(raw.Length, MaxLength)].ToString().ToLowerInvariant());
        }
        var list = set.ToList();
        list.Sort((a, b) => b.Length.CompareTo(a.Length));
        return list;
    }

    /// <summary>All (token, source) pairs for a run, de-duplicated per source.</summary>
    public static IEnumerable<(string Token, TokenSource Source)> ForRun(RunRow run, bool includePayload, int payloadCapPerRun = 512)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        AddText(run.Id.ToString("D"), set);
        AddText(run.FlowName, set, camelCase: true);
        AddText(run.TriggerKey, set);
        AddText(run.Status, set);
        AddText(run.BackgroundJobId, set);
        foreach (var t in set) yield return (t, TokenSource.Run);

        set.Clear();
        AddJsonValues(run.TriggerDataJson, set);
        foreach (var t in set) yield return (t, TokenSource.Trigger);

        set.Clear();
        foreach (var s in run.Steps) AddText(s.StepKey, set, camelCase: true);
        foreach (var t in set) yield return (t, TokenSource.StepKey);

        set.Clear();
        foreach (var s in run.Steps) AddText(s.ErrorMessage, set);
        foreach (var t in set) yield return (t, TokenSource.Error);

        if (!includePayload) yield break;
        set.Clear();
        foreach (var s in run.Steps) AddJsonValues(s.OutputJson, set, payloadCapPerRun);
        foreach (var t in set) yield return (t, TokenSource.Payload);
    }
}
