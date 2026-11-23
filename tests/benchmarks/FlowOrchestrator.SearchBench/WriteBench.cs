using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dapper;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.PostgreSQL;
using FlowOrchestrator.SqlServer;
using Microsoft.Data.SqlClient;
using Npgsql;
using ExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.SearchBench;

/// <summary>
/// Per-step write cost on the seeded (production-size) tables, through the real store methods the engine
/// calls for one step: dispatch ledger, claim, step start, output upsert, step complete, status read — plus
/// and the PROTOTYPE search-token insert. (The 1.x run used for the 2026-11-23 numbers also timed the two
/// per-step event inserts; 2.0 removed persisted events, so those timings are gone from this harness.)
/// </summary>
public static class WriteBench
{
    private sealed class Samples
    {
        private readonly Dictionary<string, List<double>> _byCall = new(StringComparer.Ordinal);

        public async Task<T> TimeAsync<T>(string call, Func<Task<T>> body)
        {
            var sw = Stopwatch.StartNew();
            var result = await body();
            sw.Stop();
            if (!_byCall.TryGetValue(call, out var list)) _byCall[call] = list = new List<double>();
            list.Add(sw.Elapsed.TotalMilliseconds);
            return result;
        }

        public Task TimeAsync(string call, Func<Task> body) => TimeAsync(call, async () => { await body(); return 0; });

        public async Task ReportAsync(string suite, string label)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"--- {suite} {label} {DateTime.UtcNow:O}");
            foreach (var (call, list) in _byCall)
            {
                list.Sort();
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{call,-34} n={list.Count,5} p50={Measure.Percentile(list, 0.5),8:F3} ms p95={Measure.Percentile(list, 0.95),8:F3} ms mean={list.Average(),8:F3} ms"));
            }
            Console.Write(sb);
            Directory.CreateDirectory(Measure.OutDir);
            await File.AppendAllTextAsync(Path.Combine(Measure.OutDir, "writes.txt"), sb.ToString());
        }
    }

    /// <param name="label">Schema state being measured, written to the report.</param>
    /// <param name="runs">Number of synthetic runs to write.</param>
    /// <param name="tokens">Also insert the step search tokens (prototype d) as a separate statement.</param>
    public static async Task MsSqlAsync(string label, int runs, bool tokens)
    {
        var db = MsSqlSeeder.Db;
        var store = new SqlFlowRunStore(db);
        IOutputsRepository outputs = (IOutputsRepository)Activator.CreateInstance(
            typeof(SqlFlowRunStore).Assembly.GetType("FlowOrchestrator.SqlServer.SqlOutputsRepository", throwOnError: true)!,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, [db], null)!;
        var s = new Samples();
        var gen = new DataGenerator(runs, seed: 777 + (int)(DateTime.UtcNow.Ticks % 1_000_000));   // fresh run ids per invocation
        var flowId = Guid.NewGuid();
        await using (var warm = new SqlConnection(db)) await warm.ExecuteScalarAsync<int>("SELECT 1");

        foreach (var run in gen.Generate())
        {
            var ctx = new ExecutionContext { RunId = run.Id };
            await s.TimeAsync("StartRunAsync", () => store.StartRunAsync(flowId, run.FlowName, run.Id, run.TriggerKey, run.TriggerDataJson, run.BackgroundJobId));
            if (tokens) await s.TimeAsync("tokens run (run+trigger)", () => MsSqlTokensAsync(db, run.Id, RunTokens(run)));
            foreach (var st in run.Steps)
            {
                var step = new StepInstance(st.StepKey, st.StepType) { RunId = run.Id };
                await s.TimeAsync("RTT SELECT 1", async () => { await using var c = new SqlConnection(db); return await c.ExecuteScalarAsync<int>("SELECT 1"); });
                await s.TimeAsync("TryRecordDispatchAsync", () => store.TryRecordDispatchAsync(run.Id, st.StepKey));
                await s.TimeAsync("TryClaimStepAsync", () => store.TryClaimStepAsync(run.Id, st.StepKey));
                await s.TimeAsync("RecordStepStartAsync", () => store.RecordStepStartAsync(run.Id, st.StepKey, st.StepType, "{\"in\":1}", null));
                await s.TimeAsync("SaveStepOutputAsync", () => outputs.SaveStepOutputAsync(ctx, null!, step, new StepResult { Key = st.StepKey, Result = st.OutputJson }).AsTask());
                await s.TimeAsync("RecordStepCompleteAsync", () => store.RecordStepCompleteAsync(run.Id, st.StepKey, st.Status, st.OutputJson, st.ErrorMessage));
                await s.TimeAsync("GetStepStatusesAsync", () => store.GetStepStatusesAsync(run.Id));
                if (tokens) await s.TimeAsync("tokens step", () => MsSqlTokensAsync(db, run.Id, StepTokens(st)));
            }
            await s.TimeAsync("CompleteRunAsync", () => store.CompleteRunAsync(run.Id, run.Status));
        }
        await s.ReportAsync("mssql", label);
    }

    public static async Task PgAsync(string label, int runs, bool tokens)
    {
        var db = PgBench.Db;
        var store = new PostgreSqlFlowRunStore(db);
        IOutputsRepository outputs = new PostgreSqlOutputsRepository(db);
        var s = new Samples();
        var gen = new DataGenerator(runs, seed: 777 + (int)(DateTime.UtcNow.Ticks % 1_000_000));   // fresh run ids per invocation
        var flowId = Guid.NewGuid();
        await using (var warm = new NpgsqlConnection(db)) await warm.ExecuteScalarAsync<int>("SELECT 1");

        foreach (var run in gen.Generate())
        {
            var ctx = new ExecutionContext { RunId = run.Id };
            await s.TimeAsync("StartRunAsync", () => store.StartRunAsync(flowId, run.FlowName, run.Id, run.TriggerKey, run.TriggerDataJson, run.BackgroundJobId));
            if (tokens) await s.TimeAsync("tokens run (run+trigger)", () => PgTokensAsync(db, run.Id, RunTokens(run)));
            foreach (var st in run.Steps)
            {
                var step = new StepInstance(st.StepKey, st.StepType) { RunId = run.Id };
                await s.TimeAsync("RTT SELECT 1", async () => { await using var c = new NpgsqlConnection(db); return await c.ExecuteScalarAsync<int>("SELECT 1"); });
                await s.TimeAsync("TryRecordDispatchAsync", () => store.TryRecordDispatchAsync(run.Id, st.StepKey));
                await s.TimeAsync("TryClaimStepAsync", () => store.TryClaimStepAsync(run.Id, st.StepKey));
                await s.TimeAsync("RecordStepStartAsync", () => store.RecordStepStartAsync(run.Id, st.StepKey, st.StepType, "{\"in\":1}", null));
                await s.TimeAsync("SaveStepOutputAsync", () => outputs.SaveStepOutputAsync(ctx, null!, step, new StepResult { Key = st.StepKey, Result = st.OutputJson }).AsTask());
                await s.TimeAsync("RecordStepCompleteAsync", () => store.RecordStepCompleteAsync(run.Id, st.StepKey, st.Status, st.OutputJson, st.ErrorMessage));
                await s.TimeAsync("GetStepStatusesAsync", () => store.GetStepStatusesAsync(run.Id));
                if (tokens) await s.TimeAsync("tokens step", () => PgTokensAsync(db, run.Id, StepTokens(st)));
            }
            await s.TimeAsync("CompleteRunAsync", () => store.CompleteRunAsync(run.Id, run.Status));
        }
        await s.ReportAsync("pg", label);
    }

    private static IStepInstance RunStep(Guid runId) => new StepInstance("__run", "__run") { RunId = runId };


    private static List<(string Token, TokenSource Source)> RunTokens(RunRow run)
    {
        var copy = new RunRow
        {
            Index = run.Index, Id = run.Id, FlowId = run.FlowId, FlowName = run.FlowName, Status = "Running", TriggerKey = run.TriggerKey,
            TriggerDataJson = run.TriggerDataJson, BackgroundJobId = run.BackgroundJobId, StartedAt = run.StartedAt, Steps = [],
        };
        return SearchTokenizer.ForRun(copy, includePayload: false).ToList();
    }

    private static List<(string Token, TokenSource Source)> StepTokens(StepRow step)
    {
        var list = new List<(string, TokenSource)>();
        var set = new HashSet<string>(StringComparer.Ordinal);
        SearchTokenizer.AddText(step.StepKey, set, camelCase: true);
        list.AddRange(set.Select(t => (t, TokenSource.StepKey)));
        set.Clear();
        SearchTokenizer.AddText(step.ErrorMessage, set);
        list.AddRange(set.Select(t => (t, TokenSource.Error)));
        set.Clear();
        SearchTokenizer.AddJsonValues(step.OutputJson, set, 512);
        list.AddRange(set.Select(t => (t, TokenSource.Payload)));
        return list;
    }

    private static async Task<int> MsSqlTokensAsync(string db, Guid runId, List<(string Token, TokenSource Source)> tokens)
    {
        if (tokens.Count == 0) return 0;
        var total = 0;
        await using var conn = new SqlConnection(db);
        // One statement per source keeps the parameter count at three regardless of the token count.
        foreach (var group in tokens.GroupBy(t => t.Source))
        {
            total += await conn.ExecuteAsync("""
                INSERT INTO FlowRunSearchTokens (Token, StartedAt, RunId, Source)
                SELECT DISTINCT s.value, fr.StartedAt, @RunId, @Source
                FROM STRING_SPLIT(@Tokens, N' ') AS s CROSS JOIN FlowRuns AS fr
                WHERE fr.Id = @RunId AND s.value <> N''
                  AND NOT EXISTS (SELECT 1 FROM FlowRunSearchTokens AS t
                                  WHERE t.Token = s.value AND t.StartedAt = fr.StartedAt AND t.RunId = @RunId AND t.Source = @Source);
                """, new { RunId = runId, Source = (byte)group.Key, Tokens = string.Join(' ', group.Select(g => g.Token)) });
        }
        return total;
    }

    private static async Task<int> PgTokensAsync(string db, Guid runId, List<(string Token, TokenSource Source)> tokens)
    {
        if (tokens.Count == 0) return 0;
        await using var conn = new NpgsqlConnection(db);
        return await conn.ExecuteAsync("""
            INSERT INTO flow_run_search_tokens (token, started_at, run_id, source)
            SELECT t.token, fr.started_at, @RunId, t.source
            FROM unnest(@Tokens::text[], @Sources::smallint[]) AS t(token, source), flow_runs fr
            WHERE fr.id = @RunId
            ON CONFLICT DO NOTHING
            """, new { RunId = runId, Tokens = tokens.Select(t => t.Token).ToArray(), Sources = tokens.Select(t => (short)t.Source).ToArray() });
    }
}
