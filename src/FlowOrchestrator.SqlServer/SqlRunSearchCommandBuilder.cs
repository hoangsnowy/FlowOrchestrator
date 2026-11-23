using System.Data;
using System.Data.SqlTypes;
using System.Text;
using Dapper;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.SqlServer;

/// <summary>
/// The batch <see cref="SqlFlowRunStore.SearchRunsAsync"/> sends for one <see cref="RunSearchQuery"/>.
/// </summary>
/// <param name="Sql">The batch text.</param>
/// <param name="Parameters">Its parameters; user input only ever reaches the batch through these.</param>
/// <param name="RowSets">Number of <c>FlowRuns</c> row sets the batch returns before the optional count.</param>
/// <param name="Merge">
/// <see langword="true"/> when the row sets must be merged in list order: each was read from the
/// cursor with <c>TOP (Skip + Take + 1)</c> and the offset is applied after the merge.
/// </param>
/// <param name="CountsTotal">Whether a final count result set follows the row sets.</param>
/// <param name="ExternalIndexText">When set, the store must also ask the registered <see cref="IRunSearchIndex"/> for this term and merge the hydrated hits.</param>
internal sealed record SqlRunSearchCommand(
    string Sql,
    DynamicParameters Parameters,
    int RowSets,
    bool Merge,
    bool CountsTotal,
    string? ExternalIndexText);

/// <summary>
/// Builds the SQL Server run-list / run-search batch. Pure: no I/O, unit-tested on its text.
/// </summary>
/// <remarks>
/// Shape rules, each measured on 200k runs / 2M steps (<c>docs/benchmarks/run-search-2026-11-23.md</c>):
/// <list type="bullet">
/// <item><b>BIN2 instead of the linguistic collation.</b> Every substring match is
/// <c>LOWER(col) COLLATE Latin1_General_100_BIN2 LIKE @LowerLike</c> with the term lower-cased once in
/// C#: one binary compare instead of a <c>CI_AS</c> comparison per character. Deep search 14.5 s → 1.4 s
/// with no schema change. Still case-insensitive; accent- and width-sensitive.</item>
/// <item><b>Narrow search column.</b> Quick search reads <c>RunSearchText</c> (id, flow name, trigger key,
/// status, job id, pre-lower-cased, BIN2) through <c>IX_FlowRuns_StartedAt_Search</c>: 88-178 ms → 5-8 ms
/// in the 7-day window.</item>
/// <item><b>Id prefix as its own statement.</b> A hex-shaped term also seeks <c>IX_FlowRuns_IdText</c>,
/// at any age, in a separate statement: glued to the text match with <c>IN (… UNION …)</c> the optimizer
/// gave up the early-stopping scan (rare terms 88 → 505 ms).</item>
/// <item><b>Keyset.</b> <c>ORDER BY StartedAt DESC, Id</c> — the order of every <c>StartedAt</c> index,
/// whose rows carry the clustered key — so a cursor is a range seek (page 2,000: 189 → 32 ms).</item>
/// <item><b>Capped count.</b> <c>COUNT(*)</c> over <c>TOP (cap + 1)</c> (10.7 → 1.1 ms at 200k runs).</item>
/// <item><b>Token table</b> (opt-in): deep search probes <c>FlowRunSearchTokens</c> by token prefix
/// (0.7-2.3 ms) and keeps the <c>LIKE</c> probe only for runs the back-fill has not reached yet.</item>
/// </list>
/// </remarks>
internal static class SqlRunSearchCommandBuilder
{
    internal const string Columns = "fr.Id, fr.FlowId, fr.FlowName, fr.Status, fr.TriggerKey, fr.TriggerDataJson, fr.BackgroundJobId, fr.StartedAt, fr.CompletedAt, fr.SourceRunId";
    private const string OrderBy = " ORDER BY fr.StartedAt DESC, fr.Id";
    private const string Bin2 = "COLLATE Latin1_General_100_BIN2";

    /// <summary>Most query tokens AND-ed in a token-table probe; further tokens are dropped (wider match).</summary>
    internal const int MaxQueryTokens = 4;

    /// <summary>Builds the batch for <paramref name="query"/> against the structures in <paramref name="schema"/>.</summary>
    /// <param name="query">The structured run query.</param>
    /// <param name="schema">Which search structures exist.</param>
    /// <param name="hasExternalIndex">An <see cref="IRunSearchIndex"/> other than the built-in token table is registered.</param>
    /// <returns>The batch.</returns>
    public static SqlRunSearchCommand Build(RunSearchQuery query, SqlRunSearchSchema schema, bool hasExternalIndex = false)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(schema);

        var p = new DynamicParameters();
        var take = ClampTake(query.Take);
        var skip = Math.Max(0, query.Skip);
        p.Add("TakePlusOne", take + 1);
        p.Add("Skip", skip);

        // Plain filters, split so that an id statement can ignore the window.
        var nonWindow = new List<string>(2);
        if (query.FlowId.HasValue) { nonWindow.Add("fr.FlowId = @FlowId"); p.Add("FlowId", query.FlowId); }
        if (!string.IsNullOrWhiteSpace(query.Status)) { nonWindow.Add("fr.Status = @Status"); p.Add("Status", query.Status); }
        var window = new List<string>(2);
        if (query.StartedFrom.HasValue) { window.Add("fr.StartedAt >= @StartedFrom"); p.Add("StartedFrom", query.StartedFrom.Value.ToUniversalTime()); }
        if (query.StartedTo.HasValue) { window.Add("fr.StartedAt <= @StartedTo"); p.Add("StartedTo", query.StartedTo.Value.ToUniversalTime()); }
        string? cursor = null;
        if (query.After is { } after)
        {
            cursor = "(fr.StartedAt < @AfterStartedAt OR (fr.StartedAt = @AfterStartedAt AND fr.Id > @AfterId))";
            p.Add("AfterStartedAt", after.StartedAt.ToUniversalTime());
            p.Add("AfterId", after.Id);
        }

        var countsTotal = query.CountsTotal;
        var sql = new StringBuilder(1024);

        if (!query.HasText)
        {
            var filters = Concat(nonWindow, window);
            if (query.RunId.HasValue) { filters.Add("fr.Id = @RunId"); p.Add("RunId", query.RunId); }
            if (cursor is not null) filters.Add(cursor);
            sql.Append("SELECT ").Append(Columns).Append(" FROM FlowRuns AS fr").Append(Where(filters))
               .Append(OrderBy).Append(" OFFSET @Skip ROWS FETCH NEXT @TakePlusOne ROWS ONLY;");
            AppendCount(sql, p, query, Concat(nonWindow, window));
            return new SqlRunSearchCommand(sql.ToString(), p, 1, false, countsTotal, null);
        }

        // ── Text search ──────────────────────────────────────────────────────────
        var text = query.Text!.Trim();
        p.Add("LowerLike", "%" + EscapeLike(text.ToLowerInvariant()) + "%");
        var textId = Guid.TryParse(text, out var parsed) ? parsed : (Guid?)null;
        var idPrefix = textId is null && schema.IdTextIndex && RunSearchQuery.IsRunIdPrefix(text);
        var deep = query.IncludeStepPayloads;
        var tokens = deep && schema.TokenTable ? RunSearchTokenizer.QueryTokens(text, MaxQueryTokens) : [];
        var useTokens = tokens.Count > 0;
        var useExternal = deep && hasExternalIndex && !useTokens;

        var statements = 1 + (textId is not null || idPrefix ? 1 : 0) + (useTokens ? 1 : 0);
        var merge = statements > 1 || useExternal;
        if (merge)
            p.Add("MergeTake", (int)Math.Min((long)skip + take + 1, int.MaxValue));

        // 1. Id statement: a full id is a primary-key seek, a hex prefix an IdText seek — both at any age.
        if (textId is not null || idPrefix)
        {
            var filters = new List<string>(nonWindow);
            if (textId is not null) { filters.Add("fr.Id = @TextId"); p.Add("TextId", textId); }
            else
            {
                // VARCHAR, not NVARCHAR: IdText is CHAR(36), and an NVARCHAR parameter would convert the column
                // and turn the seek into a scan under a SQL_* collation.
                filters.Add("fr.IdText LIKE @IdPrefix ESCAPE '\\'");
                p.Add("IdPrefix", EscapeLike(text.ToUpperInvariant()) + "%", DbType.AnsiString, size: 40);
            }
            if (cursor is not null) filters.Add(cursor);
            sql.Append("SELECT TOP (@MergeTake) ").Append(Columns).Append(" FROM FlowRuns AS fr").Append(Where(filters)).Append(OrderBy).Append(";\n");
        }

        // 2. Run columns (substring, inside the window), plus the payload LIKE probe for deep search
        //    where no index answers it.
        var runMatch = schema.SearchColumn && !text.Contains('|', StringComparison.Ordinal)
            ? "fr.RunSearchText LIKE @LowerLike ESCAPE '\\'"
            : RunColumnsBin2Match;
        string match;
        if (!deep || useExternal)
        {
            match = runMatch;
        }
        else if (!useTokens)
        {
            match = "(" + runMatch + "\n     OR " + StepsBin2Exists + ")";
        }
        else if (schema.UnindexedAtOrBefore is { } unindexed)
        {
            // Token back-fill incomplete: older runs keep the LIKE probe so results stay complete.
            match = "(" + runMatch + "\n     OR (fr.StartedAt <= @UnindexedAtOrBefore AND " + StepsBin2Exists + "))";
            p.Add("UnindexedAtOrBefore", unindexed);
        }
        else
        {
            match = runMatch;
        }

        {
            var filters = Concat(nonWindow, window);
            if (cursor is not null) filters.Add(cursor);
            filters.Add(match);
            var page = merge ? "TOP (@MergeTake) " : string.Empty;
            var offset = merge ? string.Empty : " OFFSET @Skip ROWS FETCH NEXT @TakePlusOne ROWS ONLY";
            if (schema.SearchColumn && schema.SearchIndex)
            {
                // Scan the narrow index (StartedAt, RunSearchText, FlowId, Status + Id) for the page's ids,
                // then read the full rows of those ids only. The outer order is the inner (index) order —
                // ORDER BY m.*, not fr.* — so the key lookups keep it and nothing sorts the wide rows
                // (a sort of rows carrying TriggerDataJson spills its LOBs to a worktable).
                sql.Append("SELECT ").Append(Columns).Append(" FROM (SELECT ").Append(page).Append("fr.Id, fr.StartedAt FROM FlowRuns AS fr WITH (INDEX(IX_FlowRuns_StartedAt_Search))")
                   .Append(Where(filters)).Append(OrderBy).Append(offset)
                   .Append(") AS m INNER JOIN FlowRuns AS fr ON fr.Id = m.Id ORDER BY m.StartedAt DESC, m.Id;\n");
            }
            else
            {
                sql.Append("SELECT ").Append(page).Append(Columns).Append(" FROM FlowRuns AS fr").Append(Where(filters)).Append(OrderBy).Append(offset).Append(";\n");
            }
        }

        // 3. Token table: every query token must be a prefix of one of the run's tokens.
        if (useTokens)
        {
            var filters = Concat(nonWindow, window);
            for (var i = 0; i < tokens.Count; i++)
            {
                var name = "Tok" + i;
                p.Add(name, EscapeLike(tokens[i]) + "%");
                var tokenWindow = (query.StartedFrom.HasValue ? " AND t.StartedAt >= @StartedFrom" : string.Empty)
                    + (query.StartedTo.HasValue ? " AND t.StartedAt <= @StartedTo" : string.Empty);
                filters.Add($"fr.Id IN (SELECT t.RunId FROM FlowRunSearchTokens AS t WHERE t.Token LIKE @{name} ESCAPE '\\'{tokenWindow})");
            }

            if (cursor is not null) filters.Add(cursor);
            sql.Append("SELECT TOP (@MergeTake) ").Append(Columns).Append(" FROM FlowRuns AS fr").Append(Where(filters)).Append(OrderBy).Append(";\n");
        }

        // A text search never counts (CountsTotal is false whenever Text is set).
        return new SqlRunSearchCommand(sql.ToString().TrimEnd('\n'), p, statements, merge, false, useExternal ? text : null);
    }

    /// <summary>Builds the hydration query for ids returned by an external <see cref="IRunSearchIndex"/>.</summary>
    /// <param name="query">The original query (filters, window, cursor).</param>
    /// <param name="ids">Run ids from the index.</param>
    /// <returns>A single-statement batch returning the matching rows in list order.</returns>
    public static (string Sql, DynamicParameters Parameters) BuildHydrate(RunSearchQuery query, IReadOnlyCollection<Guid> ids)
    {
        var p = new DynamicParameters();
        var filters = new List<string>(6) { "fr.Id IN @Ids" };
        p.Add("Ids", ids);
        if (query.FlowId.HasValue) { filters.Add("fr.FlowId = @FlowId"); p.Add("FlowId", query.FlowId); }
        if (!string.IsNullOrWhiteSpace(query.Status)) { filters.Add("fr.Status = @Status"); p.Add("Status", query.Status); }
        if (query.StartedFrom.HasValue) { filters.Add("fr.StartedAt >= @StartedFrom"); p.Add("StartedFrom", query.StartedFrom.Value.ToUniversalTime()); }
        if (query.StartedTo.HasValue) { filters.Add("fr.StartedAt <= @StartedTo"); p.Add("StartedTo", query.StartedTo.Value.ToUniversalTime()); }
        if (query.After is { } after)
        {
            filters.Add("(fr.StartedAt < @AfterStartedAt OR (fr.StartedAt = @AfterStartedAt AND fr.Id > @AfterId))");
            p.Add("AfterStartedAt", after.StartedAt.ToUniversalTime());
            p.Add("AfterId", after.Id);
        }

        return ("SELECT " + Columns + " FROM FlowRuns AS fr" + Where(filters) + OrderBy + ";", p);
    }

    /// <summary>
    /// Merges row sets read from the same cursor into list order (<c>StartedAt</c> descending, then
    /// <c>Id</c> in SQL Server's <c>uniqueidentifier</c> order), drops duplicates, applies the offset and
    /// keeps <c>Take + 1</c> rows.
    /// </summary>
    /// <param name="rowSets">The row sets.</param>
    /// <param name="skip">Rows to skip after the merge.</param>
    /// <param name="take">The page size.</param>
    /// <returns>Up to <c>Take + 1</c> rows in list order.</returns>
    /// <remarks>Cost: O(r log r) for r = sum of row-set sizes, each at most <c>Skip + Take + 1</c>.</remarks>
    public static List<FlowRunRecord> Merge(IReadOnlyList<IReadOnlyList<FlowRunRecord>> rowSets, int skip, int take)
    {
        var capacity = 0;
        foreach (var set in rowSets) capacity += set.Count;
        var seen = new HashSet<Guid>(capacity);
        var all = new List<FlowRunRecord>(capacity);
        foreach (var set in rowSets)
        {
            foreach (var run in set)
            {
                if (seen.Add(run.Id))
                    all.Add(run);
            }
        }

        all.Sort(CompareListOrder);
        skip = Math.Max(0, skip);
        if (skip >= all.Count)
            return [];
        var count = (int)Math.Min((long)ClampTake(take) + 1, all.Count - skip);
        return all.GetRange(skip, count);
    }

    /// <summary>List order of the SQL Server run list: <c>StartedAt</c> descending, then <c>Id</c> ascending as SQL Server orders <c>uniqueidentifier</c>.</summary>
    internal static int CompareListOrder(FlowRunRecord a, FlowRunRecord b)
    {
        var byTime = b.StartedAt.CompareTo(a.StartedAt);
        return byTime != 0 ? byTime : new SqlGuid(a.Id).CompareTo(new SqlGuid(b.Id));
    }

    /// <summary>Escapes <c>\</c>, <c>%</c>, <c>_</c> and <c>[</c> for a <c>LIKE … ESCAPE '\'</c> pattern.</summary>
    /// <param name="value">The literal text.</param>
    /// <returns>The pattern fragment matching exactly <paramref name="value"/>.</returns>
    internal static string EscapeLike(string value)
    {
        if (value.AsSpan().IndexOfAny("\\%_[") < 0)
            return value;

        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '\\' or '%' or '_' or '[')
                sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Clamps a page size to <c>[0, int.MaxValue - 1]</c> so <c>Take + 1</c> neither goes negative nor overflows.</summary>
    internal static int ClampTake(int take) => Math.Clamp(take, 0, int.MaxValue - 1);

    private static void AppendCount(StringBuilder sql, DynamicParameters p, RunSearchQuery query, List<string> plainFilters)
    {
        if (!query.CountsTotal)
            return;

        // The count covers the plain filters only — never the cursor: it is the size of the whole list.
        if (query.EffectiveTotalCountCap is { } cap)
        {
            p.Add("CountLimit", cap + 1);
            sql.Append(" SELECT COUNT(*) FROM (SELECT TOP (@CountLimit) 1 AS x FROM FlowRuns AS fr").Append(Where(plainFilters)).Append(") AS capped;");
        }
        else
        {
            sql.Append(" SELECT COUNT(*) FROM FlowRuns AS fr").Append(Where(plainFilters)).Append(';');
        }
    }

    private static List<string> Concat(List<string> a, List<string> b)
    {
        var all = new List<string>(a.Count + b.Count + 3);
        all.AddRange(a);
        all.AddRange(b);
        return all;
    }

    private static string Where(List<string> filters) => filters.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", filters);

    /// <summary>The run columns matched one by one, for a database without <c>RunSearchText</c> (or a term containing its separator).</summary>
    private const string RunColumnsBin2Match = "(LOWER(CONVERT(NVARCHAR(36), fr.Id)) " + Bin2 + " LIKE @LowerLike ESCAPE '\\'"
        + " OR LOWER(fr.FlowName) " + Bin2 + " LIKE @LowerLike ESCAPE '\\'"
        + " OR LOWER(fr.TriggerKey) " + Bin2 + " LIKE @LowerLike ESCAPE '\\'"
        + " OR LOWER(fr.Status) " + Bin2 + " LIKE @LowerLike ESCAPE '\\'"
        + " OR LOWER(fr.BackgroundJobId) " + Bin2 + " LIKE @LowerLike ESCAPE '\\')";

    /// <summary>The deep-search probe of the run's current step rows (attempt history duplicates them and is not searched).</summary>
    private const string StepsBin2Exists = "EXISTS (SELECT 1 FROM FlowSteps AS fs WHERE fs.RunId = fr.Id AND ("
        + "LOWER(fs.StepKey) " + Bin2 + " LIKE @LowerLike ESCAPE '\\'"
        + " OR LOWER(fs.ErrorMessage) " + Bin2 + " LIKE @LowerLike ESCAPE '\\'"
        + " OR LOWER(fs.OutputJson) " + Bin2 + " LIKE @LowerLike ESCAPE '\\'))";
}
