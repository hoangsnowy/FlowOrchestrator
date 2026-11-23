using System.Diagnostics;
using System.Text;
using Dapper;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.PostgreSQL;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace FlowOrchestrator.SearchBench;

/// <summary>PostgreSQL: seeding, shipped-baseline measurement and the PROTOTYPE candidate query shapes.</summary>
public static class PgBench
{
    public static string Admin => Environment.GetEnvironmentVariable("SB_PG_ADMIN")
        ?? "Host=localhost;Port=15433;Username=postgres;Password=searchbench;Database=postgres;Timeout=60";

    public static string Db => Environment.GetEnvironmentVariable("SB_PG")
        ?? "Host=localhost;Port=15433;Username=postgres;Password=searchbench;Database=flowsearch;Timeout=60;Command Timeout=1800;Maximum Pool Size=200";

    private const string RunColumns = """
        fr.id AS "Id", fr.flow_id AS "FlowId", fr.flow_name AS "FlowName", fr.status AS "Status", fr.trigger_key AS "TriggerKey",
        fr.trigger_data_json AS "TriggerDataJson", fr.background_job_id AS "BackgroundJobId", fr.started_at AS "StartedAt",
        fr.completed_at AS "CompletedAt", fr.source_run_id AS "SourceRunId"
        """;

    // ── seed ────────────────────────────────────────────────────────────────────────────────────

    public static async Task SeedAsync(int runCount, bool withEvents)
    {
        await using (var admin = new NpgsqlConnection(Admin))
        {
            await admin.ExecuteAsync("DROP DATABASE IF EXISTS flowsearch WITH (FORCE);");
            await admin.ExecuteAsync("CREATE DATABASE flowsearch;");
        }

        await new PostgreSqlFlowOrchestratorMigrator(Db, NullLogger<PostgreSqlFlowOrchestratorMigrator>.Instance).StartAsync(CancellationToken.None);

        await using var conn = new NpgsqlConnection(Db);
        await conn.OpenAsync();
        // Load without secondary indexes, then let the real migrator recreate them (CREATE INDEX IF NOT EXISTS).
        await conn.ExecuteAsync("""
            CREATE TABLE search_bench_meta (name TEXT PRIMARY KEY, value TEXT NOT NULL);
            DROP INDEX IF EXISTS ix_flow_runs_flow_name_trgm, ix_flow_runs_trigger_key_trgm, ix_flow_steps_step_key_trgm,
                ix_flow_steps_error_message_trgm, ix_flow_steps_output_json_trgm, ix_flow_runs_flow_id, ix_flow_runs_status,
                ix_flow_runs_started_at, ix_flow_runs_flow_id_status_started_at, ix_flow_steps_run_id_started_at,
                ix_flow_steps_step_key_run_id, ix_flow_events_run_id_sequence;
            """);

        var gen = new DataGenerator(runCount);
        var sw = Stopwatch.StartNew();
        int runs = 0, steps = 0, events = 0;
        long chars = 0;
        var batch = new List<RunRow>(2_000);
        foreach (var run in gen.Generate())
        {
            batch.Add(run);
            if (batch.Count < 2_000) continue;
            (int s, int e, long c) = await CopyAsync(conn, batch, withEvents);
            runs += batch.Count; steps += s; events += e; chars += c;
            batch.Clear();
            Console.WriteLine($"  {runs:N0} runs / {steps:N0} steps / {chars / 1_048_576.0:N0} M chars / {events:N0} events — {sw.Elapsed:mm\\:ss}");
        }
        if (batch.Count > 0)
        {
            (int s, int e, long c) = await CopyAsync(conn, batch, withEvents);
            runs += batch.Count; steps += s; events += e; chars += c;
        }
        Console.WriteLine($"copied {runs:N0} runs / {steps:N0} steps / {events:N0} events in {sw.Elapsed}");

        foreach (var (k, v) in gen.Needles.Values)
            await conn.ExecuteAsync("INSERT INTO search_bench_meta (name, value) VALUES (@k, @v)", new { k, v });
        await conn.ExecuteAsync("INSERT INTO search_bench_meta (name, value) VALUES ('runCount', @v)", new { v = runCount.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        sw.Restart();
        await conn.ExecuteAsync("SET maintenance_work_mem = '1GB';");
        await new PostgreSqlFlowOrchestratorMigrator(Db, NullLogger<PostgreSqlFlowOrchestratorMigrator>.Instance).StartAsync(CancellationToken.None);
        Console.WriteLine($"indexes (incl. pg_trgm GIN) rebuilt by the migrator in {sw.Elapsed}");
        await File.AppendAllTextAsync(Path.Combine(Directory.CreateDirectory(Measure.OutDir).FullName, "ddl.tsv"), $"pg\tmigrator-indexes-after-load\t{sw.Elapsed.TotalSeconds:F1}\n");
        sw.Restart();
        await conn.ExecuteAsync("VACUUM ANALYZE flow_runs; VACUUM ANALYZE flow_steps; VACUUM ANALYZE flow_events;");
        Console.WriteLine($"vacuum analyze in {sw.Elapsed}");
    }

    private static async Task<(int Steps, int Events, long Chars)> CopyAsync(NpgsqlConnection conn, List<RunRow> batch, bool withEvents)
    {
        int steps = 0, events = 0;
        long chars = 0;
        await using (var w = await conn.BeginBinaryImportAsync(
            "COPY flow_runs (id, flow_id, flow_name, status, trigger_key, trigger_data_json, background_job_id, started_at, completed_at) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var r in batch)
            {
                await w.StartRowAsync();
                await w.WriteAsync(r.Id, NpgsqlDbType.Uuid);
                await w.WriteAsync(r.FlowId, NpgsqlDbType.Uuid);
                await w.WriteAsync(r.FlowName, NpgsqlDbType.Varchar);
                await w.WriteAsync(r.Status, NpgsqlDbType.Varchar);
                await w.WriteAsync(r.TriggerKey, NpgsqlDbType.Varchar);
                await w.WriteAsync(r.TriggerDataJson, NpgsqlDbType.Text);
                await w.WriteAsync(r.BackgroundJobId, NpgsqlDbType.Varchar);
                await w.WriteAsync(r.StartedAt.UtcDateTime, NpgsqlDbType.TimestampTz);
                if (r.CompletedAt is { } c) await w.WriteAsync(c.UtcDateTime, NpgsqlDbType.TimestampTz); else await w.WriteNullAsync();
            }
            await w.CompleteAsync();
        }
        await using (var w = await conn.BeginBinaryImportAsync(
            "COPY flow_steps (run_id, step_key, step_type, status, output_json, error_message, started_at, completed_at) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var r in batch)
                foreach (var s in r.Steps)
                {
                    await w.StartRowAsync();
                    await w.WriteAsync(r.Id, NpgsqlDbType.Uuid);
                    await w.WriteAsync(s.StepKey, NpgsqlDbType.Varchar);
                    await w.WriteAsync(s.StepType, NpgsqlDbType.Varchar);
                    await w.WriteAsync(s.Status, NpgsqlDbType.Varchar);
                    if (s.OutputJson is null) await w.WriteNullAsync(); else await w.WriteAsync(s.OutputJson, NpgsqlDbType.Text);
                    if (s.ErrorMessage is null) await w.WriteNullAsync(); else await w.WriteAsync(s.ErrorMessage, NpgsqlDbType.Text);
                    await w.WriteAsync(s.StartedAt.UtcDateTime, NpgsqlDbType.TimestampTz);
                    if (s.CompletedAt is { } c) await w.WriteAsync(c.UtcDateTime, NpgsqlDbType.TimestampTz); else await w.WriteNullAsync();
                    steps++;
                    chars += s.OutputJson?.Length ?? 0;
                }
            await w.CompleteAsync();
        }
        if (withEvents)
        {
            await using var w = await conn.BeginBinaryImportAsync(
                "COPY flow_events (run_id, timestamp_utc, type, step_key, message) FROM STDIN (FORMAT BINARY)");
            foreach (var r in batch)
                foreach (var e in MsSqlSeeder.EventRows(r))
                {
                    await w.StartRowAsync();
                    await w.WriteAsync(r.Id, NpgsqlDbType.Uuid);
                    await w.WriteAsync(e.Timestamp.UtcDateTime, NpgsqlDbType.TimestampTz);
                    await w.WriteAsync(e.Type, NpgsqlDbType.Varchar);
                    await w.WriteAsync(e.StepKey, NpgsqlDbType.Varchar);
                    await w.WriteAsync(e.Message, NpgsqlDbType.Text);
                    events++;
                }
            await w.CompleteAsync();
        }
        return (steps, events, chars);
    }

    public static async Task<Needles> NeedlesAsync()
    {
        var needles = new Needles();
        await using var conn = new NpgsqlConnection(Db);
        foreach (var row in await conn.QueryAsync<(string Name, string Value)>("SELECT name, value FROM search_bench_meta"))
            needles.Values[row.Name] = row.Value;
        return needles;
    }

    // ── baseline ────────────────────────────────────────────────────────────────────────────────

    public static async Task BaselineAsync(bool deep, bool quick)
    {
        var needles = await NeedlesAsync();
        var plans = new StringBuilder();
        var scenarios = new List<(string, RunSearchQuery)>();
        if (quick) scenarios.AddRange(Scenarios.Quick(needles));
        if (deep) scenarios.AddRange(Scenarios.Deep(needles));
        foreach (var (name, query) in scenarios)
        {
            var cmd = Measure.ShippedCommand(typeof(PostgreSqlFlowRunStore), query);
            var t = await Measure.RunAsync("pg-base", name, () => RunBatchAsync(cmd.Sql, cmd.Parameters, cmd.CountsTotal), budget: TimeSpan.FromSeconds(deep ? 180 : 60));
            if (t.Min < 60_000) plans.Append(await ExplainAsync(name, cmd.Sql.Split(';')[0], cmd.Parameters));
        }
        await Measure.WriteArtifactAsync($"pg-baseline-{(deep ? "deep" : "quick")}-explain.txt", plans.ToString());
    }

    private static async Task<int> RunBatchAsync(string sql, object parameters, bool countsTotal)
    {
        await using var conn = new NpgsqlConnection(Db);
        await using var grid = await conn.QueryMultipleAsync(new CommandDefinition(sql, parameters, commandTimeout: 1_800));
        var rows = (await grid.ReadAsync()).Count();
        if (countsTotal) await grid.ReadSingleAsync<int>();
        return rows;
    }

    private static async Task<int> RunAsync(string sql, object? parameters)
    {
        await using var conn = new NpgsqlConnection(Db);
        return (await conn.QueryAsync(new CommandDefinition(sql, parameters, commandTimeout: 1_800))).Count();
    }

    public static async Task<string> ExplainAsync(string name, string sql, object? parameters)
    {
        await using var conn = new NpgsqlConnection(Db);
        var lines = await conn.QueryAsync<string>(new CommandDefinition("EXPLAIN (ANALYZE, BUFFERS) " + sql, parameters, commandTimeout: 1_800));
        return $"### {name}\n{string.Join('\n', lines)}\n\n";
    }

    // ── prototype DDL ───────────────────────────────────────────────────────────────────────────

    public static async Task SetupAsync(string candidate)
    {
        var sql = candidate switch
        {
            // (a) one lower-cased run search column with a trigram GIN index; the existing per-column trigram
            // indexes cannot serve an OR across five columns of which three have no trigram index.
            "trgm-run" => """
                ALTER TABLE flow_runs ADD COLUMN IF NOT EXISTS search_text TEXT GENERATED ALWAYS AS (
                    lower(id::text || '|' || coalesce(flow_name, '') || '|' || coalesce(trigger_key, '') || '|' || status || '|' || coalesce(background_job_id, ''))) STORED;
                CREATE INDEX IF NOT EXISTS ix_flow_runs_search_text_trgm ON flow_runs USING gin (search_text gin_trgm_ops);
                ANALYZE flow_runs;
                """,
            // (b) full text: one tsvector over step key + error + output, 'simple' config (no stemming, ids stay whole).
            "tsv" => """
                ALTER TABLE flow_steps ADD COLUMN IF NOT EXISTS search_tsv tsvector GENERATED ALWAYS AS (
                    to_tsvector('simple'::regconfig, coalesce(step_key, '') || ' ' || coalesce(error_message, '') || ' ' || left(coalesce(output_json, ''), 400000))) STORED;
                CREATE INDEX IF NOT EXISTS ix_flow_steps_search_tsv ON flow_steps USING gin (search_tsv);
                ANALYZE flow_steps;
                """,
            // (d) portable inverted index, C collation so LIKE 'abc%' is a btree range.
            "tokens" => """
                CREATE TABLE IF NOT EXISTS flow_run_search_tokens (
                    token      TEXT COLLATE "C" NOT NULL,
                    started_at TIMESTAMPTZ NOT NULL,
                    run_id     UUID NOT NULL,
                    source     SMALLINT NOT NULL,
                    PRIMARY KEY (token, started_at, run_id, source)
                );
                """,
            "drop-output-trgm" => "DROP INDEX IF EXISTS ix_flow_steps_output_json_trgm;",
            "output-trgm" => "SET maintenance_work_mem = '1GB'; CREATE INDEX IF NOT EXISTS ix_flow_steps_output_json_trgm ON flow_steps USING gin (output_json gin_trgm_ops);",
            _ => throw new ArgumentException("unknown candidate " + candidate),
        };
        var sw = Stopwatch.StartNew();
        await using var conn = new NpgsqlConnection(Db);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SET maintenance_work_mem = '1GB';");
        await conn.ExecuteAsync(sql, commandTimeout: 7_200);
        Console.WriteLine($"setup {candidate}: {sw.Elapsed}");
        await File.AppendAllTextAsync(Path.Combine(Directory.CreateDirectory(Measure.OutDir).FullName, "ddl.tsv"), $"pg\t{candidate}\t{sw.Elapsed.TotalSeconds:F1}\n");
    }

    public static async Task LoadTokensAsync(int runCount, bool includePayload)
    {
        await SetupAsync("tokens");
        await using var conn = new NpgsqlConnection(Db);
        await conn.OpenAsync();
        await conn.ExecuteAsync("TRUNCATE flow_run_search_tokens; ALTER TABLE flow_run_search_tokens DROP CONSTRAINT IF EXISTS flow_run_search_tokens_pkey;");
        var sw = Stopwatch.StartNew();
        long total = 0;
        var w = await conn.BeginBinaryImportAsync("COPY flow_run_search_tokens (token, started_at, run_id, source) FROM STDIN (FORMAT BINARY)");
        foreach (var run in new DataGenerator(runCount).Generate())
        {
            foreach (var (token, source) in SearchTokenizer.ForRun(run, includePayload))
            {
                await w.StartRowAsync();
                await w.WriteAsync(token, NpgsqlDbType.Text);
                await w.WriteAsync(run.StartedAt.UtcDateTime, NpgsqlDbType.TimestampTz);
                await w.WriteAsync(run.Id, NpgsqlDbType.Uuid);
                await w.WriteAsync((short)source, NpgsqlDbType.Smallint);
                total++;
            }
        }
        await w.CompleteAsync();
        await w.DisposeAsync();
        await conn.ExecuteAsync("SET maintenance_work_mem = '1GB'; ALTER TABLE flow_run_search_tokens ADD PRIMARY KEY (token, started_at, run_id, source); VACUUM ANALYZE flow_run_search_tokens;", commandTimeout: 7_200);
        Console.WriteLine($"tokens loaded: {total:N0} in {sw.Elapsed}");
        await File.AppendAllTextAsync(Path.Combine(Measure.OutDir, "ddl.tsv"), $"pg\ttokens-backfill(payload={includePayload})\t{sw.Elapsed.TotalSeconds:F1}\trows={total}\n");
    }

    // ── prototype queries ───────────────────────────────────────────────────────────────────────

    public static async Task CandidateAsync(string candidate)
    {
        var n = await NeedlesAsync();
        var plans = new StringBuilder();
        var scenarios = candidate switch
        {
            "trgm-run" or "uuid-range" or "tokens-quick" => Scenarios.Quick(n).Where(s => s.Query.HasText),
            "tsv" or "tokens" or "trgm-deep-windowed" => Scenarios.Deep(n),
            _ => throw new ArgumentException(candidate),
        };
        foreach (var (name, q) in scenarios)
        {
            var (sql, p) = Build(candidate, q);
            var t = await Measure.RunAsync("pg-" + candidate, name, () => RunAsync(sql, p), budget: TimeSpan.FromSeconds(120));
            if (t.Min < 60_000) plans.Append(await ExplainAsync(name, sql, p));
        }
        await Measure.WriteArtifactAsync($"pg-{candidate}-explain.txt", plans.ToString());
    }

    private static readonly System.Text.RegularExpressions.Regex HexPrefix = new("^[0-9a-fA-F-]{4,36}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static (string Sql, object Params) Build(string candidate, RunSearchQuery q)
    {
        var text = q.Text?.Trim() ?? string.Empty;
        var like = "%" + Escape(text) + "%";
        var window = q.StartedFrom.HasValue ? "fr.started_at >= @From AND " : string.Empty;
        var tokens = SearchTokenizer.QueryTokens(text);
        var (lo, hi) = UuidRange(text);
        var p = new
        {
            Like = like,
            Lower = like.ToLowerInvariant(),
            From = q.StartedFrom?.UtcDateTime,
            Take = q.Take + 1,
            Lo = lo,
            Hi = hi,
            Tsq = TsQuery(text),
            T1 = tokens.Count > 0 ? Escape(tokens[0]) + "%" : "",
            T2 = tokens.Count > 1 ? Escape(tokens[1]) + "%" : "",
        };
        const string runLike = """
            (fr.id::text ILIKE @Like OR COALESCE(fr.flow_name, '') ILIKE @Like OR COALESCE(fr.trigger_key, '') ILIKE @Like
             OR COALESCE(fr.status, '') ILIKE @Like OR COALESCE(fr.background_job_id, '') ILIKE @Like)
            """;
        switch (candidate)
        {
            case "uuid-range":
            {
                // uuid compares bytewise in canonical order, so a hex prefix IS a primary-key range: no schema change.
                var idPart = lo is not null ? "SELECT id FROM flow_runs WHERE id BETWEEN @Lo AND @Hi UNION " : string.Empty;
                return ($"""
                    SELECT {RunColumns} FROM flow_runs fr
                    WHERE fr.id IN ({idPart}SELECT fr.id FROM flow_runs fr WHERE {window}{runLike})
                    ORDER BY fr.started_at DESC LIMIT @Take
                    """, p);
            }
            case "trgm-run":
            {
                var idPart = lo is not null ? "SELECT id FROM flow_runs WHERE id BETWEEN @Lo AND @Hi UNION " : string.Empty;
                return ($"""
                    SELECT {RunColumns} FROM flow_runs fr
                    WHERE fr.id IN ({idPart}SELECT fr.id FROM flow_runs fr WHERE {window}fr.search_text LIKE @Lower)
                    ORDER BY fr.started_at DESC LIMIT @Take
                    """, p);
            }
            case "trgm-deep-windowed":
                // Shipped shape, but the step probe is joined to the window instead of a history-wide IN list.
                return ($"""
                    SELECT {RunColumns} FROM flow_runs fr
                    WHERE {window}({runLike} OR EXISTS (SELECT 1 FROM flow_steps fs WHERE fs.run_id = fr.id
                        AND (fs.step_key ILIKE @Like OR fs.error_message ILIKE @Like OR fs.output_json ILIKE @Like)))
                    ORDER BY fr.started_at DESC LIMIT @Take
                    """, p);
            case "tsv":
                return ($"""
                    SELECT {RunColumns} FROM flow_runs fr
                    WHERE {window}({runLike} OR fr.id IN (SELECT fs.run_id FROM flow_steps fs WHERE fs.search_tsv @@ to_tsquery('simple', @Tsq)))
                    ORDER BY fr.started_at DESC LIMIT @Take
                    """, p);
            case "tokens":
            case "tokens-quick":
            {
                var sources = candidate == "tokens-quick" ? " AND t.source = 1" : string.Empty;
                var tw = q.StartedFrom.HasValue ? " AND t.started_at >= @From" : string.Empty;
                var second = tokens.Count > 1
                    ? $" AND fr.id IN (SELECT t.run_id FROM flow_run_search_tokens t WHERE t.token LIKE @T2{tw}{sources})" : string.Empty;
                return ($"""
                    SELECT {RunColumns} FROM flow_runs fr
                    WHERE fr.id IN (SELECT t.run_id FROM flow_run_search_tokens t WHERE t.token LIKE @T1{tw}{sources}){second}
                    ORDER BY fr.started_at DESC LIMIT @Take
                    """, p);
            }
            default:
                throw new ArgumentException(candidate);
        }
    }

    /// <summary>Maps a hex run-id prefix to the inclusive uuid range it denotes, or (null, null).</summary>
    public static (Guid? Lo, Guid? Hi) UuidRange(string text)
    {
        if (!HexPrefix.IsMatch(text)) return (null, null);
        var hex = text.Replace("-", string.Empty, StringComparison.Ordinal);
        if (hex.Length is < 4 or > 32) return (null, null);
        static Guid Make(string h) => Guid.ParseExact($"{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..]}", "D");
        return (Make(hex.PadRight(32, '0')), Make(hex.PadRight(32, 'f')));
    }

    /// <summary><c>word:*</c> per token of the 'simple' parser, AND-ed (hyphenated ids become a phrase of parts).</summary>
    public static string TsQuery(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var terms = new List<string>();
        foreach (var w in words)
        {
            var parts = w.Split(['-', '_', '.', '@', ':', '/', '\'', '"', '(', ')', ';', ','], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            terms.Add(string.Join(" <-> ", parts.Select(x => x.ToLowerInvariant() + ":*")));
        }
        return terms.Count == 0 ? "''" : string.Join(" & ", terms.Select(t => "(" + t + ")"));
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    public static async Task KeysetAsync()
    {
        foreach (var page in new[] { 50, 2000 })
        {
            await using var conn = new NpgsqlConnection(Db);
            var cursor = await conn.QuerySingleAsync<(DateTime StartedAt, Guid Id)>(
                "SELECT started_at, id FROM flow_runs ORDER BY started_at DESC, id OFFSET @Skip LIMIT 1", new { Skip = (page - 1) * 50 - 1 });
            var keyset = $"SELECT {RunColumns} FROM flow_runs fr WHERE (fr.started_at, fr.id) < (@S, @Id) ORDER BY fr.started_at DESC, fr.id DESC LIMIT 51";
            await Measure.RunAsync("pg-keyset", $"unfiltered page {page} keyset", () => RunAsync(keyset, new { S = cursor.StartedAt, cursor.Id }));
        }
        await Measure.RunAsync("pg-count", "COUNT(*) flow_runs", () => RunAsync("SELECT COUNT(*) FROM flow_runs", null));
        await Measure.RunAsync("pg-count", "COUNT(*) WHERE status='Failed'", () => RunAsync("SELECT COUNT(*) FROM flow_runs WHERE status = 'Failed'", null));
        await Measure.RunAsync("pg-count", "reltuples estimate", () => RunAsync("SELECT reltuples::bigint FROM pg_class WHERE oid = 'flow_runs'::regclass", null));
    }

    public static async Task SizesAsync()
    {
        await using var conn = new NpgsqlConnection(Db);
        var rows = await conn.QueryAsync<(string Name, string Table, long Bytes)>("""
            SELECT c.relname, COALESCE(t.relname, c.relname), pg_total_relation_size(c.oid)
            FROM pg_class c LEFT JOIN pg_index i ON i.indexrelid = c.oid LEFT JOIN pg_class t ON t.oid = i.indrelid
            JOIN pg_namespace ns ON ns.oid = c.relnamespace AND ns.nspname = 'public'
            WHERE c.relkind IN ('r', 'i') AND COALESCE(t.relname, c.relname) IN ('flow_runs', 'flow_steps', 'flow_events', 'flow_run_search_tokens')
            ORDER BY 2, 1
            """);
        var sb = new StringBuilder();
        foreach (var r in rows) sb.AppendLine($"{r.Table,-24} {r.Name,-44} {r.Bytes / 1_048_576.0,10:N1} MB");
        Console.Write(sb);
        await File.AppendAllTextAsync(Path.Combine(Directory.CreateDirectory(Measure.OutDir).FullName, "pg-sizes.txt"), $"--- {DateTime.UtcNow:O}\n{sb}");
    }
}
