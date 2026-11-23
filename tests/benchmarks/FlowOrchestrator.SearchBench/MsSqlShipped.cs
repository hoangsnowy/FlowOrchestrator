using System.Diagnostics;
using Dapper;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowOrchestrator.SearchBench;

/// <summary>
/// Hooks that measure the SHIPPED 2.0 run-search schema on the seeded database (not the prototypes):
/// the migrator's additive columns + online index build, and the token back-fill.
/// After <c>mssql-migrate</c>, <c>mssql-base quick|deep</c> measures the shipped query text again, since
/// it calls <c>SqlFlowRunStore.BuildSearchRunsCommand</c>.
/// </summary>
public static class MsSqlShipped
{
    /// <summary>Runs the shipped migrator and waits for its background index build; prints the elapsed time.</summary>
    /// <param name="tokens">Also create the token tables (<c>UseRunSearchIndex()</c>).</param>
    public static async Task MigrateAsync(bool tokens)
    {
        var sw = Stopwatch.StartNew();
        using var migrator = new FlowOrchestratorSqlMigrator(MsSqlSeeder.Db, NullLogger<FlowOrchestratorSqlMigrator>.Instance, tokens);
        await migrator.StartAsync(CancellationToken.None);
        var startup = sw.Elapsed;
        await migrator.BackgroundIndexBuild;
        Console.WriteLine($"migrate(tokens={tokens}): startup blocked {startup}, indexes ready after {sw.Elapsed}");
        await File.AppendAllTextAsync(Path.Combine(Measure.OutDir, "ddl.tsv"),
            $"mssql\tshipped-migrate(tokens={tokens})\t{startup.TotalSeconds:F1}\t{sw.Elapsed.TotalSeconds:F1}\n");
    }

    /// <summary>Runs the shipped back-fill from scratch to completion; prints duration and token count.</summary>
    /// <param name="batchSize">Runs per batch.</param>
    public static async Task BackfillAsync(int batchSize)
    {
        await using (var conn = new SqlConnection(MsSqlSeeder.Db))
        {
            await conn.ExecuteAsync("""
                TRUNCATE TABLE FlowRunSearchTokens;
                UPDATE FlowRunSearchIndexState SET BackfillAfterStartedAt = NULL, BackfillAfterId = NULL, BackfillCompletedAt = NULL WHERE Id = 1;
                """, commandTimeout: 3_600);
        }

        var options = new RunSearchIndexOptions { Enabled = true, BackfillBatchSize = batchSize, BackfillBatchDelay = TimeSpan.Zero };
        var service = new SqlRunSearchIndexBackfillService(
            MsSqlSeeder.Db, new SqlServerRunSearchTokenIndex(MsSqlSeeder.Db, options), NullLogger<SqlRunSearchIndexBackfillService>.Instance);
        var sw = Stopwatch.StartNew();
        await service.StartAsync(CancellationToken.None);
        await service.BackfillPass;
        sw.Stop();
        await service.StopAsync(CancellationToken.None);

        await using var check = new SqlConnection(MsSqlSeeder.Db);
        var rows = await check.ExecuteScalarAsync<long>("SELECT COUNT_BIG(*) FROM FlowRunSearchTokens");
        Console.WriteLine($"backfill(batch={batchSize}): {sw.Elapsed}, {rows:N0} token rows");
        await File.AppendAllTextAsync(Path.Combine(Measure.OutDir, "ddl.tsv"),
            $"mssql\tshipped-backfill(batch={batchSize})\t{sw.Elapsed.TotalSeconds:F1}\trows={rows}\n");
    }
}
