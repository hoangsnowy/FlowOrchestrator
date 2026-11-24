using System.Data;
using System.Diagnostics;
using Dapper;
using FlowOrchestrator.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowOrchestrator.SearchBench;

/// <summary>Creates the <c>FlowSearch</c> database with the real migrator and bulk-loads the generated data set.</summary>
public static class MsSqlSeeder
{
    public static string Master => Environment.GetEnvironmentVariable("SB_MSSQL_MASTER")
        ?? "Server=localhost,14333;User Id=sa;Password=SearchBench!2026;TrustServerCertificate=true;Database=master;Connect Timeout=60";

    public static string Db => Environment.GetEnvironmentVariable("SB_MSSQL")
        ?? "Server=localhost,14333;User Id=sa;Password=SearchBench!2026;TrustServerCertificate=true;Database=FlowSearch;Connect Timeout=60;Max Pool Size=200";

    public static async Task SeedAsync(int runCount, bool withEvents)
    {
        await using (var master = new SqlConnection(Master))
        {
            await master.ExecuteAsync("""
                IF DB_ID('FlowSearch') IS NOT NULL BEGIN ALTER DATABASE FlowSearch SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE FlowSearch; END
                CREATE DATABASE FlowSearch;
                ALTER DATABASE FlowSearch SET RECOVERY SIMPLE;
                """, commandTimeout: 600);
        }

        using (var migrator = new FlowOrchestratorSqlMigrator(Db, NullLogger<FlowOrchestratorSqlMigrator>.Instance))
        {
            await migrator.StartAsync(CancellationToken.None);
            await migrator.BackgroundIndexBuild;
        }

        // Load into the 1.x run-table shape: `mssql-migrate` then measures the 2.0 migration on the loaded table.
        await using (var pre = new SqlConnection(Db))
        {
            await pre.ExecuteAsync("""
                DROP INDEX IF EXISTS IX_FlowRuns_IdText ON FlowRuns;
                DROP INDEX IF EXISTS IX_FlowRuns_StartedAt_Search ON FlowRuns;
                ALTER TABLE FlowRuns DROP COLUMN IF EXISTS RunSearchText, IdText;
                """);
        }

        await using var conn = new SqlConnection(Db);
        await conn.OpenAsync();
        await conn.ExecuteAsync("CREATE TABLE SearchBenchMeta (Name NVARCHAR(64) NOT NULL PRIMARY KEY, Value NVARCHAR(400) NOT NULL);");

        var gen = new DataGenerator(runCount);
        var sw = Stopwatch.StartNew();
        long chars = 0;
        int runs = 0, steps = 0, events = 0;
        using var runTable = RunTable();
        using var stepTable = StepTable();
        using var eventTable = EventTable();
        foreach (var run in gen.Generate())
        {
            runTable.Rows.Add(run.Id, run.FlowId, run.FlowName, run.Status, run.TriggerKey, run.TriggerDataJson, run.BackgroundJobId, run.StartedAt, (object?)run.CompletedAt ?? DBNull.Value);
            foreach (var s in run.Steps)
            {
                stepTable.Rows.Add(run.Id, s.StepKey, s.StepType, s.Status, (object?)s.OutputJson ?? DBNull.Value, (object?)s.ErrorMessage ?? DBNull.Value, s.StartedAt, (object?)s.CompletedAt ?? DBNull.Value);
                chars += s.OutputJson?.Length ?? 0;
            }
            if (withEvents)
                foreach (var e in EventRows(run)) { eventTable.Rows.Add(run.Id, e.Timestamp, e.Type, (object?)e.StepKey ?? DBNull.Value, e.Message); events++; }
            runs++;
            steps += run.Steps.Count;
            if (runTable.Rows.Count >= 2_000)
            {
                await FlushAsync(conn, runTable, stepTable, eventTable);
                Console.WriteLine($"  {runs:N0} runs / {steps:N0} steps / {chars / 1_048_576.0:N0} M chars output / {events:N0} events — {sw.Elapsed:mm\\:ss}");
            }
        }
        await FlushAsync(conn, runTable, stepTable, eventTable);
        Console.WriteLine($"seeded {runs:N0} runs / {steps:N0} steps / {chars / 1_048_576.0:N0} M chars output / {events:N0} events in {sw.Elapsed}");

        foreach (var (k, v) in gen.Needles.Values)
            await conn.ExecuteAsync("INSERT INTO SearchBenchMeta (Name, Value) VALUES (@k, @v)", new { k, v });
        await conn.ExecuteAsync("INSERT INTO SearchBenchMeta (Name, Value) VALUES ('runCount', @v)", new { v = runCount.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        sw.Restart();
        await conn.ExecuteAsync("UPDATE STATISTICS FlowRuns WITH FULLSCAN; UPDATE STATISTICS FlowSteps WITH FULLSCAN; IF OBJECT_ID('FlowEvents') IS NOT NULL UPDATE STATISTICS FlowEvents WITH FULLSCAN;", commandTimeout: 3_600);
        Console.WriteLine($"statistics updated in {sw.Elapsed}");
    }

    public static IEnumerable<(DateTimeOffset Timestamp, string Type, string? StepKey, string Message)> EventRows(RunRow run)
    {
        // Mirrors the engine's RecordEventAsync calls: run.started, step.started + step.completed|failed per
        // executed step, run.completed. Skipped-by-upstream steps get one step.skipped.
        yield return (run.StartedAt, "run.started", "__run", $"Flow run started via trigger '{run.TriggerKey}'.");
        foreach (var s in run.Steps)
        {
            if (s.Status == "Skipped" && s.ErrorMessage is not null)
            {
                yield return (s.StartedAt, "step.skipped", s.StepKey, $"Step '{s.StepKey}' skipped: {s.ErrorMessage}");
                continue;
            }
            yield return (s.StartedAt, "step.started", s.StepKey, $"Step '{s.StepKey}' started.");
            if (s.Status == "Running") continue;
            yield return s.Status == "Failed"
                ? (s.CompletedAt ?? s.StartedAt, "step.failed", s.StepKey, s.ErrorMessage ?? $"Step '{s.StepKey}' failed.")
                : (s.CompletedAt ?? s.StartedAt, "step.completed", s.StepKey, $"Step '{s.StepKey}' completed with status {s.Status}.");
        }
        if (run.CompletedAt is { } done)
            yield return (done, "run.completed", "__run", $"Run completed with status {run.Status}.");
    }

    public static async Task<Needles> LoadNeedlesAsync(SqlConnection conn)
    {
        var needles = new Needles();
        foreach (var row in await conn.QueryAsync<(string Name, string Value)>("SELECT Name, Value FROM SearchBenchMeta"))
            needles.Values[row.Name] = row.Value;
        return needles;
    }

    private static async Task FlushAsync(SqlConnection conn, DataTable runs, DataTable steps, DataTable events)
    {
        await BulkAsync(conn, "FlowRuns", runs);
        await BulkAsync(conn, "FlowSteps", steps);
        if (events.Rows.Count > 0) await BulkAsync(conn, "FlowEvents", events);
        runs.Clear();
        steps.Clear();
        events.Clear();
    }

    private static async Task BulkAsync(SqlConnection conn, string table, DataTable data)
    {
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, null) { DestinationTableName = table, BatchSize = 5_000, BulkCopyTimeout = 3_600 };
        foreach (DataColumn column in data.Columns)
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        await bulk.WriteToServerAsync(data);
    }

    private static DataTable RunTable()
    {
        var t = new DataTable();
        t.Columns.Add("Id", typeof(Guid));
        t.Columns.Add("FlowId", typeof(Guid));
        t.Columns.Add("FlowName", typeof(string));
        t.Columns.Add("Status", typeof(string));
        t.Columns.Add("TriggerKey", typeof(string));
        t.Columns.Add("TriggerDataJson", typeof(string));
        t.Columns.Add("BackgroundJobId", typeof(string));
        t.Columns.Add("StartedAt", typeof(DateTimeOffset));
        t.Columns.Add("CompletedAt", typeof(DateTimeOffset));
        return t;
    }

    private static DataTable StepTable()
    {
        var t = new DataTable();
        t.Columns.Add("RunId", typeof(Guid));
        t.Columns.Add("StepKey", typeof(string));
        t.Columns.Add("StepType", typeof(string));
        t.Columns.Add("Status", typeof(string));
        t.Columns.Add("OutputJson", typeof(string));
        t.Columns.Add("ErrorMessage", typeof(string));
        t.Columns.Add("StartedAt", typeof(DateTimeOffset));
        t.Columns.Add("CompletedAt", typeof(DateTimeOffset));
        return t;
    }

    private static DataTable EventTable()
    {
        var t = new DataTable();
        t.Columns.Add("RunId", typeof(Guid));
        t.Columns.Add("Timestamp", typeof(DateTimeOffset));
        t.Columns.Add("Type", typeof(string));
        t.Columns.Add("StepKey", typeof(string));
        t.Columns.Add("Message", typeof(string));
        return t;
    }
}
