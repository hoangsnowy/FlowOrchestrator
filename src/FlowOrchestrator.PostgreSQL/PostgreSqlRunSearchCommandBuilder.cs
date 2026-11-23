using System.Text;
using Dapper;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.PostgreSQL;

/// <summary>The batch <see cref="PostgreSqlFlowRunStore.SearchRunsAsync"/> sends for one <see cref="RunSearchQuery"/>.</summary>
/// <param name="Sql">The batch text.</param>
/// <param name="Parameters">Its parameters; user input only ever reaches the batch through these.</param>
/// <param name="RowSets">Number of <c>flow_runs</c> row sets the batch returns before the optional count.</param>
/// <param name="Merge">
/// <see langword="true"/> when the row sets must be merged in list order: each was read from the cursor
/// with <c>LIMIT Skip + Take + 1</c> and the offset is applied after the merge.
/// </param>
/// <param name="CountsTotal">Whether a final count result set follows the row sets.</param>
internal sealed record PostgreSqlRunSearchCommand(string Sql, DynamicParameters Parameters, int RowSets, bool Merge, bool CountsTotal);

/// <summary>
/// Builds the PostgreSQL run-list / run-search batch. Pure: no I/O, unit-tested on its text.
/// </summary>
/// <remarks>
/// Shape rules, measured on 200k runs / 2M steps (<c>docs/benchmarks/run-search-2026-11-23.md</c>, PostgreSQL):
/// <list type="bullet">
/// <item><b>Run columns through one trigram-indexed expression.</b> Quick search matches
/// <see cref="RunSearchExpression"/> (id, flow name, trigger key, status, job id, lower-cased) with
/// <c>LIKE @LowerLike</c>; the migrator's <c>ix_flow_runs_run_search_trgm</c> GIN index answers it
/// (~1 ms for every quick search, windowed or not, against 16-150 ms). Without <c>pg_trgm</c> the same
/// text is a sequential scan — correct, only slower.</item>
/// <item><b>Run-id prefix as its own statement.</b> A hex-shaped prefix is a primary-key range
/// (<c>uuid</c> compares bytewise, so the text prefix is a key range), at any age, like the SQL Server
/// <c>IdText</c> seek. Glued into the text statement with <c>IN (… UNION …)</c> it stayed ~100 ms.</item>
/// <item><b>Deep search as a correlated <c>EXISTS</c></b> over bare step columns: the planner walks the
/// window newest-first for a common word and stops after a page (33 s → 5 ms), and still picks the step
/// trigram bitmap for a rare term.</item>
/// <item><b>Keyset</b> <c>ORDER BY started_at DESC, id</c> and a <b>capped count</b>.</item>
/// </list>
/// </remarks>
internal static class PostgreSqlRunSearchCommandBuilder
{
    internal const string Columns = """
        fr.id AS "Id", fr.flow_id AS "FlowId", fr.flow_name AS "FlowName",
               fr.status AS "Status", fr.trigger_key AS "TriggerKey",
               fr.trigger_data_json AS "TriggerDataJson", fr.background_job_id AS "BackgroundJobId",
               fr.started_at AS "StartedAt", fr.completed_at AS "CompletedAt",
               fr.source_run_id AS "SourceRunId"
        """;

    private const string OrderBy = " ORDER BY fr.started_at DESC, fr.id";

    /// <summary>
    /// The lower-cased run search text. The migrator indexes exactly this expression
    /// (<c>ix_flow_runs_run_search_trgm</c>, unqualified columns); PostgreSQL only uses an expression
    /// index when the query repeats the expression, so both are produced here.
    /// </summary>
    /// <param name="alias">Table alias with its dot (<c>"fr."</c>), or empty for the index definition.</param>
    /// <returns>The SQL expression.</returns>
    internal static string RunSearchExpression(string alias)
        => $"lower({alias}id::text || '|' || coalesce({alias}flow_name, '') || '|' || coalesce({alias}trigger_key, '') || '|' || {alias}status || '|' || coalesce({alias}background_job_id, ''))";

    /// <summary>Builds the batch for <paramref name="query"/>.</summary>
    /// <param name="query">The structured run query.</param>
    /// <returns>The batch.</returns>
    public static PostgreSqlRunSearchCommand Build(RunSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var p = new DynamicParameters();
        var take = ClampTake(query.Take);
        var skip = Math.Max(0, query.Skip);
        p.Add("TakePlusOne", take + 1);
        p.Add("Skip", skip);

        var nonWindow = new List<string>(2);
        if (query.FlowId.HasValue) { nonWindow.Add("fr.flow_id = @FlowId"); p.Add("FlowId", query.FlowId); }
        if (!string.IsNullOrWhiteSpace(query.Status)) { nonWindow.Add("fr.status = @Status"); p.Add("Status", query.Status); }
        var window = new List<string>(2);
        // UTC: Npgsql rejects a non-zero offset for timestamptz.
        if (query.StartedFrom.HasValue) { window.Add("fr.started_at >= @StartedFrom"); p.Add("StartedFrom", query.StartedFrom.Value.ToUniversalTime()); }
        if (query.StartedTo.HasValue) { window.Add("fr.started_at <= @StartedTo"); p.Add("StartedTo", query.StartedTo.Value.ToUniversalTime()); }
        string? cursor = null;
        if (query.After is { } after)
        {
            cursor = "(fr.started_at < @AfterStartedAt OR (fr.started_at = @AfterStartedAt AND fr.id > @AfterId))";
            p.Add("AfterStartedAt", after.StartedAt.ToUniversalTime());
            p.Add("AfterId", after.Id);
        }

        var sql = new StringBuilder(1024);
        if (!query.HasText)
        {
            var filters = Concat(nonWindow, window);
            if (query.RunId.HasValue) { filters.Add("fr.id = @RunId"); p.Add("RunId", query.RunId); }
            if (cursor is not null) filters.Add(cursor);
            sql.Append("SELECT ").Append(Columns).Append(" FROM flow_runs AS fr").Append(Where(filters))
               .Append(OrderBy).Append(" LIMIT @TakePlusOne OFFSET @Skip;");
            AppendCount(sql, p, query, Concat(nonWindow, window));
            return new PostgreSqlRunSearchCommand(sql.ToString(), p, 1, false, query.CountsTotal);
        }

        var text = query.Text!.Trim();
        p.Add("LowerLike", "%" + EscapeLike(text.ToLowerInvariant()) + "%");
        var textId = Guid.TryParse(text, out var parsed) ? parsed : (Guid?)null;
        var hasRange = TryRunIdRange(text, out var lo, out var hi);
        var statements = 1 + (textId is not null || hasRange ? 1 : 0);
        var merge = statements > 1;
        if (merge)
            p.Add("MergeTake", (int)Math.Min((long)skip + take + 1, int.MaxValue));

        // 1. Id statement, at any age: a full id is a key lookup, a prefix a key range.
        if (textId is not null || hasRange)
        {
            var filters = new List<string>(nonWindow);
            if (textId is not null) { filters.Add("fr.id = @TextId"); p.Add("TextId", textId); }
            else { filters.Add("fr.id BETWEEN @IdLow AND @IdHigh"); p.Add("IdLow", lo); p.Add("IdHigh", hi); }
            if (cursor is not null) filters.Add(cursor);
            sql.Append("SELECT ").Append(Columns).Append(" FROM flow_runs AS fr").Append(Where(filters))
               .Append(OrderBy).Append(" LIMIT @MergeTake;\n");
        }

        // 2. Run columns (substring, inside the window), plus the correlated step probe for deep search.
        string runMatch;
        if (text.Contains('|', StringComparison.Ordinal))
        {
            // The expression joins the columns with '|': match column by column so a term cannot span two.
            runMatch = "(lower(fr.id::text) LIKE @LowerLike OR lower(fr.flow_name) LIKE @LowerLike OR lower(fr.trigger_key) LIKE @LowerLike"
                + " OR lower(fr.status) LIKE @LowerLike OR lower(fr.background_job_id) LIKE @LowerLike)";
        }
        else
        {
            runMatch = RunSearchExpression("fr.") + " LIKE @LowerLike";
        }

        var match = runMatch;
        if (query.IncludeStepPayloads)
        {
            // Bare columns with ILIKE, so the step trigram indexes stay usable for rare terms.
            p.Add("SearchLike", "%" + EscapeLike(text) + "%");
            match = "(" + runMatch + "\n     OR EXISTS (SELECT 1 FROM flow_steps AS fs WHERE fs.run_id = fr.id AND ("
                + "fs.step_key ILIKE @SearchLike OR fs.error_message ILIKE @SearchLike OR fs.output_json ILIKE @SearchLike)))";
        }

        {
            var filters = Concat(nonWindow, window);
            if (cursor is not null) filters.Add(cursor);
            filters.Add(match);
            sql.Append("SELECT ").Append(Columns).Append(" FROM flow_runs AS fr").Append(Where(filters)).Append(OrderBy)
               .Append(merge ? " LIMIT @MergeTake;" : " LIMIT @TakePlusOne OFFSET @Skip;");
        }

        return new PostgreSqlRunSearchCommand(sql.ToString(), p, statements, merge, false);
    }

    /// <summary>
    /// Maps a run-id prefix in canonical <c>D</c> form to the inclusive primary-key range of every id that
    /// starts with it: the prefix padded with <c>0</c> and with <c>f</c>.
    /// </summary>
    /// <param name="text">The trimmed term.</param>
    /// <param name="low">Smallest id with the prefix.</param>
    /// <param name="high">Largest id with the prefix.</param>
    /// <returns>
    /// <see langword="false"/> unless <see cref="RunSearchQuery.IsRunIdPrefix"/> holds and the hyphens sit
    /// where the canonical form has them (so the match equals SQL Server's <c>IdText LIKE 'prefix%'</c>).
    /// </returns>
    internal static bool TryRunIdRange(string text, out Guid low, out Guid high)
    {
        low = high = Guid.Empty;
        if (!RunSearchQuery.IsRunIdPrefix(text))
            return false;

        const string Template = "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx";
        Span<char> lo = stackalloc char[36];
        Span<char> hi = stackalloc char[36];
        for (var i = 0; i < 36; i++)
        {
            var isHyphen = Template[i] == '-';
            if (i < text.Length)
            {
                var c = text[i];
                if (isHyphen ? c != '-' : !char.IsAsciiHexDigit(c))
                    return false;
                lo[i] = hi[i] = char.ToLowerInvariant(c);
            }
            else
            {
                lo[i] = isHyphen ? '-' : '0';
                hi[i] = isHyphen ? '-' : 'f';
            }
        }

        return Guid.TryParseExact(lo, "D", out low) && Guid.TryParseExact(hi, "D", out high);
    }

    /// <summary>
    /// Merges row sets read from the same cursor into list order (<c>started_at</c> descending, then
    /// <c>id</c> in PostgreSQL's bytewise <c>uuid</c> order), drops duplicates, applies the offset and keeps
    /// <c>Take + 1</c> rows.
    /// </summary>
    /// <param name="rowSets">The row sets.</param>
    /// <param name="skip">Rows to skip after the merge.</param>
    /// <param name="take">The page size.</param>
    /// <returns>Up to <c>Take + 1</c> rows in list order.</returns>
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
        return all.GetRange(skip, (int)Math.Min((long)ClampTake(take) + 1, all.Count - skip));
    }

    /// <summary>List order: <c>started_at</c> descending, then <c>id</c> ascending as PostgreSQL orders <c>uuid</c> (bytewise, canonical order).</summary>
    internal static int CompareListOrder(FlowRunRecord a, FlowRunRecord b)
    {
        var byTime = b.StartedAt.CompareTo(a.StartedAt);
        if (byTime != 0)
            return byTime;
        Span<byte> x = stackalloc byte[16];
        Span<byte> y = stackalloc byte[16];
        a.Id.TryWriteBytes(x, bigEndian: true, out _);
        b.Id.TryWriteBytes(y, bigEndian: true, out _);
        return x.SequenceCompareTo(y);
    }

    /// <summary>Escapes <c>\</c>, <c>%</c> and <c>_</c> for PostgreSQL's default <c>LIKE</c> escape character.</summary>
    internal static string EscapeLike(string value)
    {
        if (value.AsSpan().IndexOfAny("\\%_") < 0)
            return value;

        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '\\' or '%' or '_')
                sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }

    internal static int ClampTake(int take) => Math.Clamp(take, 0, int.MaxValue - 1);

    private static void AppendCount(StringBuilder sql, DynamicParameters p, RunSearchQuery query, List<string> plainFilters)
    {
        if (!query.CountsTotal)
            return;

        // The count covers the plain filters only — never the cursor: it is the size of the whole list.
        if (query.EffectiveTotalCountCap is { } cap)
        {
            p.Add("CountLimit", cap + 1);
            sql.Append(" SELECT COUNT(*)::int FROM (SELECT 1 FROM flow_runs AS fr").Append(Where(plainFilters)).Append(" LIMIT @CountLimit) AS capped;");
        }
        else
        {
            sql.Append(" SELECT COUNT(*)::int FROM flow_runs AS fr").Append(Where(plainFilters)).Append(';');
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
}
