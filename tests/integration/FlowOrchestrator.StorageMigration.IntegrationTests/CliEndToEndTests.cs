using FlowOrchestrator.Cli;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.StorageMigration.IntegrationTests;

/// <summary>
/// The runbook, through the command line against real databases: dry run, copy, verify-only, and a second
/// copy that is refused because the target is no longer empty. Connection strings come from <c>env:NAME</c>.
/// </summary>
public sealed class CliEndToEndTests
{
    private readonly DatabaseFixture _db;

    public CliEndToEndTests(DatabaseFixture db) => _db = db;

    [Fact]
    public async Task Dry_run_copy_verify_and_a_refused_second_copy_exit_with_their_codes()
    {
        // Arrange
        var sourceCs = await _db.CreateSqlServerDatabaseAsync();
        var targetCs = await _db.CreatePostgreSqlDatabaseAsync();
        await using (var host = await FlowHost.StartAsync(FlowStorageProvider.SqlServer, sourceCs))
        {
            await host.RunAsync<OrderFlow>(new { label = "cli", amount = 3, items = new[] { "1", "2" } });
            await host.RunAsync<FailingFlow>(null);
        }

        var env = new Dictionary<string, string> { ["SRC"] = sourceCs, ["DST"] = targetCs };
        string[] baseArgs = ["storage", "copy", "--from", "sqlserver", "--from-connection", "env:SRC", "--to", "postgresql", "--to-connection", "env:DST"];
        async Task<(int Code, string Out, string Err)> Run(params string[] extra)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var code = await CliApp.RunAsync([.. baseArgs, .. extra], stdout, stderr, name => env.GetValueOrDefault(name), CancellationToken.None);
            return (code, stdout.ToString(), stderr.ToString());
        }

        // Act
        var dryRun = await Run("--dry-run");
        var copy = await Run("--batch-size", "2");
        var verify = await Run("--verify-only");
        var again = await Run();

        // Assert
        Assert.Equal(0, dryRun.Code);
        Assert.Contains("Dry run finished", dryRun.Out);
        Assert.Equal(0, copy.Code);
        Assert.Contains("all tables verified", copy.Out);
        Assert.Equal(0, verify.Code);
        Assert.Contains("Verification finished", verify.Out);
        Assert.Equal(3, again.Code);
        Assert.Contains("Refused (TargetNotEmpty)", again.Err);
        Assert.DoesNotContain(sourceCs, copy.Out + copy.Err);
        await using var target = StoreReader.Create(FlowStorageProvider.PostgreSql, targetCs);
        var (runs, total) = await target.Runs.GetRunsPageAsync(null, null, 0, 10);
        Assert.Equal(2, total);
        Assert.Contains(runs, r => r.Status == RunStatusNames.Failed);
    }
}
