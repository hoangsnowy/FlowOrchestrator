using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.SqlServer;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SearchBench;

/// <summary>SQL Server: shipped-baseline measurement and the PROTOTYPE candidate query shapes.</summary>
public static partial class MsSqlBench
{
    private const string RunColumns = "fr.Id, fr.FlowId, fr.FlowName, fr.Status, fr.TriggerKey, fr.TriggerDataJson, fr.BackgroundJobId, fr.StartedAt, fr.CompletedAt, fr.SourceRunId";

    private static string Db => MsSqlSeeder.Db;

    // ── baseline ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Measures every scenario with the store's shipped SQL, and captures STATISTICS IO + actual plans.</summary>
    public static async Task BaselineAsync(bool deep, bool quick)
    {
        var needles = await NeedlesAsync();
        var plans = new StringBuilder();
        var scenarios = new List<(string, RunSearchQuery)>();
        if (quick) scenarios.AddRange(Scenarios.Quick(needles));
        if (deep) scenarios.AddRange(Scenarios.Deep(needles));
        foreach (var (name, query) in scenarios)
        {
            var cmd = Measure.ShippedCommand(typeof(SqlFlowRunStore), query);
            var t = await Measure.RunAsync("mssql-base", name, () => RunBatchAsync(cmd.Sql, cmd.Parameters, cmd.CountsTotal),
                budget: TimeSpan.FromSeconds(deep ? 180 : 60));
            if (t.Min < 60_000) plans.Append(await CaptureAsync(name, cmd.Sql, cmd.Parameters));
        }
        await Measure.WriteArtifactAsync($"mssql-baseline-{(deep ? "deep" : "quick")}-io.txt", plans.ToString());
    }

    public static async Task<Needles> NeedlesAsync()
    {
        await using var conn = new SqlConnection(Db);
        return await MsSqlSeeder.LoadNeedlesAsync(conn);
    }

    private static async Task<int> RunBatchAsync(string sql, object parameters, bool countsTotal)
    {
        await using var conn = new SqlConnection(Db);
        using var grid = await conn.QueryMultipleAsync(new CommandDefinition(sql, parameters, commandTimeout: 900));
        // Drain every result set: the shipped 2.0 batch may hold an id-prefix and a token statement too.
        var rows = 0;
        while (!grid.IsConsumed)
            rows += (await grid.ReadAsync()).Count();
        return countsTotal ? rows - 1 : rows;
    }

    private static async Task<int> RunAsync(string sql, object? parameters)
    {
        await using var conn = new SqlConnection(Db);
        return (await conn.QueryAsync(new CommandDefinition(sql, parameters, commandTimeout: 900))).Count();
    }

    /// <summary>One execution with STATISTICS IO/TIME and the actual plan's operator summary.</summary>
    public static async Task<string> CaptureAsync(string name, string sql, object? parameters)
    {
        var messages = new List<string>();
        await using var conn = new SqlConnection(Db);
        conn.InfoMessage += (_, e) => { lock (messages) messages.Add(e.Message); };
        await conn.OpenAsync();
        await conn.ExecuteAsync("SET STATISTICS IO ON; SET STATISTICS TIME ON; SET STATISTICS XML ON;");
        var planXml = new List<string>();
        using (var reader = await conn.ExecuteReaderAsync(new CommandDefinition(sql, parameters, commandTimeout: 900)))
        {
            do
            {
                if (reader.FieldCount == 1 && reader.GetName(0).StartsWith("Microsoft SQL Server", StringComparison.Ordinal))
                {
                    while (await reader.ReadAsync()) planXml.Add(reader.GetString(0));
                }
                else
                {
                    while (await reader.ReadAsync()) { }
                }
            } while (await reader.NextResultAsync());
        }
        await conn.ExecuteAsync("SET STATISTICS XML OFF; SET STATISTICS IO OFF; SET STATISTICS TIME OFF;");
        var sb = new StringBuilder();
        sb.AppendLine($"### {name}");
        lock (messages)
            foreach (var m in messages)
                if (m.StartsWith("Table", StringComparison.Ordinal) || m.Contains("CPU time", StringComparison.Ordinal))
                    sb.AppendLine("  " + m.Trim().Replace("\n", " ", StringComparison.Ordinal));
        foreach (var xml in planXml) sb.AppendLine("  ops: " + SummarisePlan(xml));
        sb.AppendLine();
        return sb.ToString();
    }

    private static string SummarisePlan(string xml)
    {
        // PhysicalOp + object for each RelOp, in document order: enough to see seek vs scan vs key lookup.
        var ops = new List<string>();
        foreach (Match m in RelOp().Matches(xml))
        {
            var op = m.Groups["op"].Value;
            var rest = xml.AsSpan(m.Index, Math.Min(1_500, xml.Length - m.Index)).ToString();
            var idx = IndexRef().Match(rest);
            ops.Add(idx.Success ? $"{op}[{idx.Groups["t"].Value}.{idx.Groups["i"].Value}]" : op);
        }
        return string.Join(" > ", ops);
    }

    [GeneratedRegex("<RelOp [^>]*PhysicalOp=\"(?<op>[^\"]+)\"")]
    private static partial Regex RelOp();

    [GeneratedRegex("Table=\"\\[(?<t>[^\\]]+)\\]\"(?: Index=\"\\[(?<i>[^\\]]+)\\]\")?")]
    private static partial Regex IndexRef();

    // ── prototype DDL ───────────────────────────────────────────────────────────────────────────

    /// <summary>Applies one prototype's schema change and prints how long the migration took.</summary>
    public static async Task SetupAsync(string candidate)
    {
        var sql = candidate switch
        {
            // (e) run-id prefix: SQL Server orders uniqueidentifier by its LAST six bytes first, so a textual
            // prefix is not a key range. An index on the canonical text form is. Non-persisted computed column:
            // no table rewrite, only an index build.
            "idtext" => """
                IF COL_LENGTH('FlowRuns', 'IdText') IS NULL
                    ALTER TABLE FlowRuns ADD IdText AS CONVERT(CHAR(36), Id);
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FlowRuns_IdText')
                    EXEC('CREATE INDEX IX_FlowRuns_IdText ON FlowRuns(IdText)');
                """,
            // (e) narrow, pre-lower-cased, binary-collated search column covered by a StartedAt index:
            // the %term% residual becomes one BIN2 compare per row instead of five linguistic ones.
            "searchcol" => """
                IF COL_LENGTH('FlowRuns', 'RunSearchText') IS NULL
                    ALTER TABLE FlowRuns ADD RunSearchText AS
                        CAST(LOWER(CONCAT(CONVERT(CHAR(36), Id), N'|', FlowName, N'|', TriggerKey, N'|', Status, N'|', BackgroundJobId)) AS NVARCHAR(800))
                        COLLATE Latin1_General_100_BIN2;
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FlowRuns_StartedAt_Search')
                    EXEC('CREATE INDEX IX_FlowRuns_StartedAt_Search ON FlowRuns(StartedAt DESC) INCLUDE (RunSearchText)');
                """,
            // (c) Full-Text Search. FlowSteps' key is composite, FTS needs a unique single-column key: add one.
            "fts" => """
                IF COL_LENGTH('FlowSteps', 'SearchKey') IS NULL
                    ALTER TABLE FlowSteps ADD SearchKey BIGINT IDENTITY(1,1) NOT NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_FlowSteps_SearchKey')
                    EXEC('CREATE UNIQUE INDEX UX_FlowSteps_SearchKey ON FlowSteps(SearchKey)');
                IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'fo_search')
                    CREATE FULLTEXT CATALOG fo_search;
                IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('FlowSteps'))
                    EXEC('CREATE FULLTEXT INDEX ON FlowSteps (StepKey LANGUAGE 0, ErrorMessage LANGUAGE 0, OutputJson LANGUAGE 0)
                          KEY INDEX UX_FlowSteps_SearchKey ON fo_search WITH CHANGE_TRACKING AUTO, STOPLIST OFF');
                """,
            // (d) portable inverted index. Clustered on (Token, StartedAt DESC) so an exact token is an ordered
            // newest-first range and a prefix is a range seek.
            "tokens" => """
                IF OBJECT_ID('FlowRunSearchTokens') IS NULL
                    CREATE TABLE FlowRunSearchTokens (
                        Token     NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                        StartedAt DATETIMEOFFSET   NOT NULL,
                        RunId     UNIQUEIDENTIFIER NOT NULL,
                        Source    TINYINT          NOT NULL,
                        CONSTRAINT PK_FlowRunSearchTokens PRIMARY KEY CLUSTERED (Token, StartedAt DESC, RunId, Source)
                    );
                """,
            _ => throw new ArgumentException("unknown candidate " + candidate),
        };
        var sw = Stopwatch.StartNew();
        await using var conn = new SqlConnection(Db);
        await conn.ExecuteAsync(sql, commandTimeout: 7_200);
        Console.WriteLine($"setup {candidate}: {sw.Elapsed}");
        await File.AppendAllTextAsync(Path.Combine(Measure.OutDir, "ddl.tsv"), $"mssql\t{candidate}\t{sw.Elapsed.TotalSeconds:F1}\n");
        if (candidate == "fts") await WaitForFtsPopulationAsync(sw);
    }

    private static async Task WaitForFtsPopulationAsync(Stopwatch sw)
    {
        await using var conn = new SqlConnection(Db);
        while (true)
        {
            var status = await conn.ExecuteScalarAsync<int>("SELECT CAST(FULLTEXTCATALOGPROPERTY('fo_search', 'PopulateStatus') AS INT)");
            var items = await conn.ExecuteScalarAsync<long>("SELECT CAST(OBJECTPROPERTYEX(OBJECT_ID('FlowSteps'), 'TableFulltextItemCount') AS BIGINT)");
            Console.WriteLine($"  fts populate status={status} items={items:N0} {sw.Elapsed:hh\\:mm\\:ss}");
            if (status == 0) break;
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
        Console.WriteLine($"fts populated: {sw.Elapsed}");
        await File.AppendAllTextAsync(Path.Combine(Measure.OutDir, "ddl.tsv"), $"mssql\tfts-populated\t{sw.Elapsed.TotalSeconds:F1}\n");
    }

    /// <summary>Bulk-loads the inverted index from the regenerated data set (the backfill a migration would run).</summary>
    public static async Task LoadTokensAsync(int runCount, bool includePayload)
    {
        await SetupAsync("tokens");
        await using var conn = new SqlConnection(Db);
        await conn.OpenAsync();
        await conn.ExecuteAsync("TRUNCATE TABLE FlowRunSearchTokens;");
        using var table = new DataTable();
        table.Columns.Add("Token", typeof(string));
        table.Columns.Add("StartedAt", typeof(DateTimeOffset));
        table.Columns.Add("RunId", typeof(Guid));
        table.Columns.Add("Source", typeof(byte));
        var sw = Stopwatch.StartNew();
        long total = 0;
        var perSource = new long[6];
        foreach (var run in new DataGenerator(runCount).Generate())
        {
            foreach (var (token, source) in SearchTokenizer.ForRun(run, includePayload))
            {
                table.Rows.Add(token, run.StartedAt, run.Id, (byte)source);
                perSource[(int)source]++;
            }
            if (table.Rows.Count >= 200_000)
            {
                total += table.Rows.Count;
                await BulkTokensAsync(conn, table);
                Console.WriteLine($"  tokens {total:N0} {sw.Elapsed:mm\\:ss}");
            }
        }
        total += table.Rows.Count;
        await BulkTokensAsync(conn, table);
        await conn.ExecuteAsync("ALTER INDEX PK_FlowRunSearchTokens ON FlowRunSearchTokens REBUILD; UPDATE STATISTICS FlowRunSearchTokens WITH FULLSCAN;", commandTimeout: 7_200);
        Console.WriteLine($"tokens loaded: {total:N0} rows ({string.Join(", ", Enumerable.Range(1, 5).Select(s => $"{(TokenSource)s}={perSource[s]:N0}"))}) in {sw.Elapsed}");
        await File.AppendAllTextAsync(Path.Combine(Measure.OutDir, "ddl.tsv"), $"mssql\ttokens-backfill(payload={includePayload})\t{sw.Elapsed.TotalSeconds:F1}\trows={total}\n");
    }

    private static async Task BulkTokensAsync(SqlConnection conn, DataTable table)
    {
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, null) { DestinationTableName = "FlowRunSearchTokens", BatchSize = 50_000, BulkCopyTimeout = 3_600 };
        foreach (DataColumn c in table.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
        await bulk.WriteToServerAsync(table);
        table.Clear();
    }

    // ── prototype queries ───────────────────────────────────────────────────────────────────────

    /// <summary>Measures one candidate over the scenarios it applies to.</summary>
    public static async Task CandidateAsync(string candidate)
    {
        var n = await NeedlesAsync();
        var plans = new StringBuilder();
        var scenarios = candidate switch
        {
            "idtext" or "searchcol" or "collate" => Scenarios.Quick(n).Where(s => s.Query.HasText),
            "fts" or "tokens" or "tokens-quick" or "collate-deep" => Scenarios.Deep(n),
            "keyset" => Scenarios.Quick(n).Where(s => !s.Query.HasText),
            _ => throw new ArgumentException(candidate),
        };
        if (candidate == "tokens-quick") scenarios = Scenarios.Quick(n).Where(s => s.Query.HasText);
        foreach (var (name, q) in scenarios)
        {
            var (sql, p) = Build(candidate, q, n);
            var t = await Measure.RunAsync("mssql-" + candidate, name, () => RunAsync(sql, p), budget: TimeSpan.FromSeconds(120));
            if (t.Min < 60_000) plans.Append(await CaptureAsync(name, sql, p));
        }
        await Measure.WriteArtifactAsync($"mssql-{candidate}-io.txt", plans.ToString());
    }

    private static readonly Regex HexPrefix = new("^[0-9a-fA-F-]{4,36}$", RegexOptions.Compiled);

    public static (string Sql, object Params) Build(string candidate, RunSearchQuery q, Needles n)
    {
        var text = q.Text?.Trim() ?? string.Empty;
        var like = "%" + Escape(text) + "%";
        var lower = like.ToLowerInvariant();
        var window = q.StartedFrom.HasValue ? "fr.StartedAt >= @From AND " : string.Empty;
        var take = q.Take + 1;
        object p = new { Like = like, Lower = lower, From = q.StartedFrom, Prefix = Escape(text) + "%", Take = take, Fts = FtsQuery(text), T1 = Tok(text, 0), T2 = Tok(text, 1) };
        var runLike = """
            (CAST(fr.Id AS NVARCHAR(36)) LIKE @Like ESCAPE '\' OR ISNULL(fr.FlowName, '') LIKE @Like ESCAPE '\'
             OR ISNULL(fr.TriggerKey, '') LIKE @Like ESCAPE '\' OR ISNULL(fr.Status, '') LIKE @Like ESCAPE '\'
             OR ISNULL(fr.BackgroundJobId, '') LIKE @Like ESCAPE '\')
            """;
        switch (candidate)
        {
            case "idtext":
            {
                // Hex-shaped term: id-prefix seek over ALL history, UNION the ordinary windowed text match (a
                // numeric job id is hex-shaped too, so the text match must still run).
                var idPart = HexPrefix.IsMatch(text) ? $"SELECT Id FROM FlowRuns WHERE IdText LIKE @Prefix ESCAPE '\\' UNION " : string.Empty;
                return ($"""
                    SELECT TOP (@Take) {RunColumns} FROM FlowRuns fr
                    WHERE fr.Id IN ({idPart}SELECT fr.Id FROM FlowRuns fr WHERE {window}{runLike})
                    ORDER BY fr.StartedAt DESC
                    """, p);
            }
            case "searchcol":
            {
                var idPart = HexPrefix.IsMatch(text) ? $"SELECT Id FROM FlowRuns WHERE IdText LIKE @Prefix ESCAPE '\\' UNION ALL " : string.Empty;
                var core = $"SELECT TOP (@Take) fr.Id, fr.StartedAt FROM FlowRuns fr WITH (INDEX(IX_FlowRuns_StartedAt_Search)) WHERE {window}fr.RunSearchText LIKE @Lower ESCAPE '\\' ORDER BY fr.StartedAt DESC";
                return (idPart.Length == 0
                    ? $"SELECT {RunColumns} FROM ({core}) m JOIN FlowRuns fr ON fr.Id = m.Id ORDER BY fr.StartedAt DESC"
                    : $"SELECT TOP (@Take) {RunColumns} FROM FlowRuns fr WHERE fr.Id IN ({idPart}SELECT Id FROM ({core}) m) ORDER BY fr.StartedAt DESC", p);
            }
            case "collate-deep":
                return ($"""
                    SELECT TOP (@Take) {RunColumns} FROM FlowRuns fr
                    WHERE {window}({runLike} OR EXISTS (SELECT 1 FROM FlowSteps fs WHERE fs.RunId = fr.Id AND (
                        LOWER(fs.StepKey) COLLATE Latin1_General_100_BIN2 LIKE @Lower ESCAPE '\'
                        OR LOWER(fs.ErrorMessage) COLLATE Latin1_General_100_BIN2 LIKE @Lower ESCAPE '\'
                        OR LOWER(fs.OutputJson) COLLATE Latin1_General_100_BIN2 LIKE @Lower ESCAPE '\')))
                    ORDER BY fr.StartedAt DESC
                    """, p);
            case "fts":
                return ($"""
                    SELECT TOP (@Take) {RunColumns} FROM FlowRuns fr
                    WHERE {window}({runLike} OR fr.Id IN (SELECT fs.RunId FROM FlowSteps fs WHERE CONTAINS((fs.StepKey, fs.ErrorMessage, fs.OutputJson), @Fts)))
                    ORDER BY fr.StartedAt DESC
                    """, p);
            case "tokens":
            case "tokens-quick":
            {
                var sources = candidate == "tokens-quick" ? " AND t.Source IN (1)" : string.Empty;
                var tw = q.StartedFrom.HasValue ? " AND t.StartedAt >= @From" : string.Empty;
                var second = Tok(text, 1) is null ? string.Empty
                    : $" AND fr.Id IN (SELECT t.RunId FROM FlowRunSearchTokens t WHERE t.Token LIKE @T2 ESCAPE '\\'{tw}{sources})";
                return ($"""
                    SELECT TOP (@Take) {RunColumns} FROM FlowRuns fr
                    WHERE fr.Id IN (SELECT t.RunId FROM FlowRunSearchTokens t WHERE t.Token LIKE @T1 ESCAPE '\'{tw}{sources}){second}
                    ORDER BY fr.StartedAt DESC
                    """, p);
            }
            case "keyset":
                throw new NotSupportedException("keyset is measured by KeysetAsync");
            default:
                throw new ArgumentException(candidate);
        }
    }

    private static string? Tok(string text, int i)
    {
        var tokens = SearchTokenizer.QueryTokens(text);
        return i < tokens.Count ? Escape(tokens[i]) + "%" : null;
    }

    /// <summary>One <c>"word*"</c> prefix term per whitespace-separated word, AND-ed.</summary>
    public static string FtsQuery(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length == 0 ? "\"\"" : string.Join(" AND ", words.Select(w => "\"" + w.Replace("\"", string.Empty, StringComparison.Ordinal) + "*\""));
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal);

    /// <summary>OFFSET vs keyset at page 50 and page 2000, and total-count alternatives.</summary>
    public static async Task KeysetAsync()
    {
        foreach (var page in new[] { 50, 2000 })
        {
            var skip = (page - 1) * 50;
            await using var conn = new SqlConnection(Db);
            var cursor = await conn.QuerySingleAsync<(DateTimeOffset StartedAt, Guid Id)>(
                "SELECT StartedAt, Id FROM FlowRuns ORDER BY StartedAt DESC, Id OFFSET @Skip ROWS FETCH NEXT 1 ROWS ONLY", new { Skip = skip - 1 });
            const string keyset = $"""
                SELECT TOP (51) {RunColumns} FROM FlowRuns fr
                WHERE fr.StartedAt < @S OR (fr.StartedAt = @S AND fr.Id > @Id)
                ORDER BY fr.StartedAt DESC, fr.Id
                """;
            await Measure.RunAsync("mssql-keyset", $"unfiltered page {page} keyset", () => RunAsync(keyset, new { S = cursor.StartedAt, cursor.Id }));
        }
        await Measure.RunAsync("mssql-count", "COUNT(*) FlowRuns", () => RunAsync("SELECT COUNT(*) FROM FlowRuns", null));
        await Measure.RunAsync("mssql-count", "COUNT(*) WHERE Status='Failed'", () => RunAsync("SELECT COUNT(*) FROM FlowRuns WHERE Status = 'Failed'", null));
        await Measure.RunAsync("mssql-count", "capped COUNT (TOP 10001)", () => RunAsync("SELECT COUNT(*) FROM (SELECT TOP (10001) 1 AS x FROM FlowRuns) c", null));
        await Measure.RunAsync("mssql-count", "dm_db_partition_stats row_count", () => RunAsync(
            "SELECT SUM(row_count) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('FlowRuns') AND index_id IN (0, 1)", null));
    }

    /// <summary>Table / index sizes for the report.</summary>
    public static async Task SizesAsync()
    {
        await using var conn = new SqlConnection(Db);
        var rows = await conn.QueryAsync<(string Table, string Index, long Rows, decimal Mb)>("""
            SELECT t.name, ISNULL(i.name, '(heap)'), SUM(CASE WHEN p.index_id IN (0,1) OR i.type = 2 THEN p.rows ELSE 0 END) / COUNT(DISTINCT a.type),
                   CAST(SUM(a.total_pages) * 8 / 1024.0 AS DECIMAL(12,1))
            FROM sys.tables t
            JOIN sys.indexes i ON i.object_id = t.object_id
            JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id
            JOIN sys.allocation_units a ON a.container_id = p.partition_id
            WHERE t.name IN ('FlowRuns', 'FlowSteps', 'FlowEvents', 'FlowRunSearchTokens')
            GROUP BY t.name, i.name ORDER BY t.name, i.name
            """);
        var sb = new StringBuilder();
        foreach (var r in rows) sb.AppendLine($"{r.Table,-22} {r.Index,-40} rows={r.Rows,12:N0} {r.Mb,10:N1} MB");
        var fts = await conn.QueryAsync<(string Catalog, int SizeMb, int Items)>(
            "SELECT name, CAST(FULLTEXTCATALOGPROPERTY(name, 'IndexSize') AS INT), CAST(FULLTEXTCATALOGPROPERTY(name, 'ItemCount') AS INT) FROM sys.fulltext_catalogs");
        foreach (var f in fts) sb.AppendLine($"FTS catalog {f.Catalog}: {f.SizeMb} MB, {f.Items:N0} items");
        Console.Write(sb);
        await File.AppendAllTextAsync(Path.Combine(Measure.OutDir, "mssql-sizes.txt"), $"--- {DateTime.UtcNow:O}\n{sb}");
    }
}
