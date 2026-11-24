using System.Diagnostics;
using FlowOrchestrator.PostgreSQL;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowOrchestrator.SearchBench;

/// <summary>
/// Hook that applies the SHIPPED 2.0 PostgreSQL migrator to the seeded database and times its startup
/// part and its background <c>CREATE INDEX CONCURRENTLY</c>. After it, <c>pg-base quick|deep</c> measures
/// the shipped query text (it calls <c>PostgreSqlFlowRunStore.BuildSearchRunsCommand</c>).
/// </summary>
public static class PgShipped
{
    /// <summary>Runs the shipped migrator and waits for the run-search index; prints both times.</summary>
    public static async Task MigrateAsync()
    {
        var sw = Stopwatch.StartNew();
        using var migrator = new PostgreSqlFlowOrchestratorMigrator(PgBench.Db, NullLogger<PostgreSqlFlowOrchestratorMigrator>.Instance);
        await migrator.StartAsync(CancellationToken.None);
        var startup = sw.Elapsed;
        await migrator.BackgroundIndexBuild;
        Console.WriteLine($"pg migrate: startup blocked {startup}, search index ready after {sw.Elapsed}");
        await File.AppendAllTextAsync(Path.Combine(Measure.OutDir, "ddl.tsv"),
            $"pg\tshipped-migrate\t{startup.TotalSeconds:F1}\t{sw.Elapsed.TotalSeconds:F1}\n");
    }
}
