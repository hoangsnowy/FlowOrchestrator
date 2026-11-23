namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Structured run-list query for <see cref="IFlowRunStore.SearchRunsAsync"/>. Every filter is
/// AND-combined; unset filters are ignored.
/// </summary>
/// <remarks>
/// The query is deliberately structured rather than a single free-text string: deciding that a term
/// is a run id or a status name happens once, in the caller, so every provider gets the same index
/// seeks instead of a substring scan. Providers must not reinterpret <see cref="Text"/> as a status;
/// the only id handling of <see cref="Text"/> is the documented equality and prefix match.
/// </remarks>
public sealed record RunSearchQuery
{
    /// <summary>Restricts results to a single flow.</summary>
    public Guid? FlowId { get; init; }

    /// <summary>Restricts results to runs whose status equals this canonical token (e.g. <c>"Succeeded"</c>).</summary>
    public string? Status { get; init; }

    /// <summary>
    /// Exact run-id match. Providers compare the key column by equality, which is a primary-key seek;
    /// never a cast-and-substring match.
    /// </summary>
    public Guid? RunId { get; init; }

    /// <summary>
    /// Case-insensitive substring matched against the run's own columns: id, flow name, trigger key,
    /// status and background job id. When the text parses as a GUID the id column is compared by
    /// equality instead of a substring match.
    /// </summary>
    /// <remarks>
    /// <para>On the SQL Server and in-memory providers, a term that looks like the start of a run id (see
    /// <see cref="IsRunIdPrefix"/>, e.g. the eight characters the dashboard's run list shows) additionally
    /// matches every run whose id starts with it, <b>regardless of <see cref="StartedFrom"/> /
    /// <see cref="StartedTo"/></b>: an id prefix is an index seek, so an old run is found without widening
    /// the window. Other providers match the id as a substring inside the window.</para>
    /// <para>Case folding is ordinal (<c>ToLowerInvariant</c> on both sides). The SQL Server provider
    /// compares binary after lower-casing, so it is accent- and width-sensitive: <c>é</c> does not
    /// match <c>e</c> and <c>ß</c> does not match <c>ss</c>, unlike the database's linguistic
    /// <c>CI_AS</c> collation. Identifiers, codes and ASCII text match exactly as before.</para>
    /// </remarks>
    public string? Text { get; init; }

    /// <summary>Shortest term treated as a run-id prefix; shorter hex strings match too many ids to be useful.</summary>
    public const int MinRunIdPrefixLength = 4;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="text"/> can be the start of a run id in its
    /// canonical <c>D</c> form: <see cref="MinRunIdPrefixLength"/> to 35 characters, hex digits and
    /// hyphens only, at least one hex digit. A full 36-character id is an equality match instead.
    /// </summary>
    /// <param name="text">The trimmed search term.</param>
    /// <returns>Whether providers should also run the id-prefix match for this term.</returns>
    public static bool IsRunIdPrefix(ReadOnlySpan<char> text)
    {
        if (text.Length < MinRunIdPrefixLength || text.Length >= 36)
            return false;

        var hexDigits = 0;
        foreach (var c in text)
        {
            if (char.IsAsciiHexDigit(c)) hexDigits++;
            else if (c != '-') return false;
        }

        return hexDigits > 0;
    }

    /// <summary>
    /// When <see langword="true"/>, <see cref="Text"/> is also matched against the run's current step
    /// rows (step key, error message, output JSON). Off by default: it reads every step payload in the
    /// window, so callers should only set it together with a bounded <see cref="StartedFrom"/>.
    /// </summary>
    public bool IncludeStepPayloads { get; init; }

    /// <summary>Inclusive lower bound on the run's start time.</summary>
    public DateTimeOffset? StartedFrom { get; init; }

    /// <summary>Inclusive upper bound on the run's start time.</summary>
    public DateTimeOffset? StartedTo { get; init; }

    /// <summary>Number of leading rows to skip (offset pagination, newest first).</summary>
    /// <remarks>
    /// Kept for compatibility and for jumping to an arbitrary page. An offset re-reads every skipped
    /// row (page 2,000 of 200k runs on SQL Server: 189 ms, against 32 ms with <see cref="After"/>), and
    /// the page shifts when new runs start in between. Prefer <see cref="After"/> for next-page
    /// navigation. When both are set, <see cref="Skip"/> counts rows after the cursor.
    /// </remarks>
    public int Skip { get; init; }

    /// <summary>
    /// Keyset cursor: return only rows that sort after this position (the
    /// <see cref="RunSearchPage.NextCursor"/> of the previous page). <see langword="null"/> starts at
    /// the newest run.
    /// </summary>
    public RunSearchCursor? After { get; init; }

    /// <summary>Maximum number of rows to return.</summary>
    public int Take { get; init; } = 50;

    /// <summary>
    /// Requests an exact <see cref="RunSearchPage.TotalCount"/>. Honoured only when neither
    /// <see cref="Text"/> nor <see cref="RunId"/> is set, so the count stays an index-only count over
    /// the plain filters; a text search reports <see cref="RunSearchPage.HasMore"/> instead.
    /// </summary>
    public bool IncludeTotal { get; init; }

    /// <summary>
    /// Upper bound for the <see cref="IncludeTotal"/> count. When greater than zero the store counts
    /// at most <c>TotalCountCap + 1</c> rows; if more match, <see cref="RunSearchPage.TotalCount"/> is
    /// <see cref="TotalCountCap"/> and <see cref="RunSearchPage.TotalIsLowerBound"/> is
    /// <see langword="true"/> (render it as "10,000+"). <see langword="null"/> or non-positive counts exactly.
    /// </summary>
    /// <remarks>
    /// An exact <c>COUNT(*)</c> grows linearly with retention (10.7 ms of an 11.9 ms page at 200k runs on
    /// SQL Server); the capped count stops after the cap (1.1 ms) and is enough for page navigation.
    /// </remarks>
    public int? TotalCountCap { get; init; }

    /// <summary><see langword="true"/> when <see cref="Text"/> carries a non-blank term.</summary>
    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    /// <summary><see langword="true"/> when the store should compute <see cref="RunSearchPage.TotalCount"/>.</summary>
    public bool CountsTotal => IncludeTotal && !HasText && RunId is null;

    /// <summary><see cref="TotalCountCap"/> when it is a usable positive cap; otherwise <see langword="null"/> (exact count).</summary>
    public int? EffectiveTotalCountCap => TotalCountCap is > 0 and < int.MaxValue ? TotalCountCap : null;
}

/// <summary>One page of <see cref="IFlowRunStore.SearchRunsAsync"/> results, newest first.</summary>
/// <param name="Runs">Rows for the requested page; at most <see cref="RunSearchQuery.Take"/>.</param>
/// <param name="HasMore">
/// <see langword="true"/> when at least one more matching row exists after this page. Providers learn
/// this by reading <c>Take + 1</c> rows, so it never costs a count.
/// </param>
/// <param name="TotalCount">
/// Exact number of matching rows, or <see langword="null"/> when the query did not ask for it or
/// carried a text / run-id term (see <see cref="RunSearchQuery.CountsTotal"/>).
/// </param>
public sealed record RunSearchPage(IReadOnlyList<FlowRunRecord> Runs, bool HasMore, int? TotalCount)
{
    /// <summary>
    /// <see langword="true"/> when <see cref="TotalCount"/> stopped at <see cref="RunSearchQuery.TotalCountCap"/>:
    /// more rows than that match.
    /// </summary>
    public bool TotalIsLowerBound { get; init; }

    /// <summary>
    /// Cursor that continues after the last row of this page; set when <see cref="HasMore"/> is
    /// <see langword="true"/>. Pass it as <see cref="RunSearchQuery.After"/> to read the next page.
    /// </summary>
    public RunSearchCursor? NextCursor { get; init; }

    /// <summary>
    /// Builds a page from rows read in list order with <c>Take + 1</c>: trims the extra row, derives
    /// <see cref="HasMore"/> and <see cref="NextCursor"/>, and applies the count cap to <paramref name="total"/>.
    /// </summary>
    /// <param name="rows">Rows in list order. Trimmed in place to <paramref name="take"/>.</param>
    /// <param name="take">The page size.</param>
    /// <param name="total">The counted total, or <see langword="null"/> when no count was taken. A capped count has read at most <c>cap + 1</c> rows.</param>
    /// <param name="totalCountCap">The cap the count ran with, or <see langword="null"/> for an exact count.</param>
    /// <returns>The page.</returns>
    public static RunSearchPage FromRows(List<FlowRunRecord> rows, int take, int? total, int? totalCountCap)
    {
        ArgumentNullException.ThrowIfNull(rows);
        take = Math.Max(0, take);
        var hasMore = rows.Count > take;
        if (hasMore)
            rows.RemoveRange(take, rows.Count - take);

        var lowerBound = false;
        if (total is { } counted && totalCountCap is { } cap && counted > cap)
        {
            total = cap;
            lowerBound = true;
        }

        return new RunSearchPage(rows, hasMore, total)
        {
            TotalIsLowerBound = lowerBound,
            NextCursor = hasMore && rows.Count > 0 ? RunSearchCursor.After(rows[^1]) : null,
        };
    }
}
