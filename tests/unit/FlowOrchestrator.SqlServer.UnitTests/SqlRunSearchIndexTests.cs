using System.Text.Json;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowOrchestrator.SqlServer.Tests;

/// <summary>
/// Docker-free checks of the opt-in token index (tokenization, standalone probe), the migrator's
/// run-search DDL, and how <c>UseSqlServer</c> wires the index.
/// </summary>
public sealed class SqlRunSearchIndexTests
{
    private const string ConnectionString = "Server=.;Database=test;";

    private static SqlServerRunSearchTokenIndex NewIndex(int maxTokens = RunSearchTokenizer.DefaultMaxTokensPerDocument)
        => new(ConnectionString, new RunSearchIndexOptions { Enabled = true, MaxTokensPerDocument = maxTokens });

    private static List<(string Token, int Source)> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => (e.GetProperty("t").GetString()!, e.GetProperty("s").GetInt32()))
            .ToList();
    }

    [Fact]
    public void TokenizeStep_tags_each_token_with_its_source()
    {
        // Arrange
        var index = NewIndex();

        // Act
        var json = index.TokenizeStep("ChargeCard", "Gateway timeout for INV-77", """{"paymentId":"PAY-9"}""");

        // Assert
        var tokens = Parse(json!);
        Assert.Contains(("chargecard", (int)RunSearchTokenSource.StepKey), tokens);
        Assert.Contains(("charge", (int)RunSearchTokenSource.StepKey), tokens);
        Assert.Contains(("inv-77", (int)RunSearchTokenSource.Error), tokens);
        Assert.Contains(("pay-9", (int)RunSearchTokenSource.Payload), tokens);
    }

    [Fact]
    public void TokenizeRun_indexes_trigger_values_and_nothing_for_an_empty_body()
    {
        // Arrange
        var index = NewIndex();

        // Act
        var withBody = index.TokenizeRun("""{"orderId":"ORD-1"}""");
        var empty = index.TokenizeRun(null);

        // Assert
        Assert.Contains(("ord-1", (int)RunSearchTokenSource.Trigger), Parse(withBody!));
        Assert.Null(empty);
    }

    [Fact]
    public void TokenizeStep_respects_the_per_document_cap()
    {
        // Arrange
        var index = NewIndex(maxTokens: 3);
        var output = JsonSerializer.Serialize(Enumerable.Range(0, 50).ToDictionary(i => "k" + i, i => "VAL-" + i));

        // Act
        var json = index.TokenizeStep("s", null, output);

        // Assert
        Assert.True(Parse(json!).Count(t => t.Source == (int)RunSearchTokenSource.Payload) <= 3);
    }

    [Fact]
    public void Insert_statement_is_best_effort_and_compiled_at_run_time()
    {
        // Arrange

        // Act
        var sql = SqlServerRunSearchTokenIndex.InsertTokensSql;

        // Assert — sp_executesql inside TRY/CATCH: a missing table or a pre-2016 compatibility level is a
        // caught error, never a compile failure of the run-start / step-completion batch.
        Assert.Contains("BEGIN TRY", sql);
        Assert.Contains("EXEC sp_executesql", sql);
        Assert.Contains("OPENJSON(@j)", sql);
        Assert.Contains("BEGIN CATCH", sql);
    }

    [Fact]
    public void Standalone_probe_ands_every_token_and_returns_newest_first()
    {
        // Arrange
        var query = new RunSearchIndexQuery("deadlock victim", true, DateTimeOffset.UnixEpoch, null, 50);

        // Act
        var (sql, parameters) = SqlServerRunSearchTokenIndex.BuildSearch(RunSearchTokenizer.QueryTokens(query.Text), query);

        // Assert
        Assert.Contains("t.Token LIKE @Tok0", sql);
        Assert.Contains("u.Token LIKE @Tok1", sql);
        Assert.Contains("u.StartedAt >= @StartedFrom", sql);
        Assert.Contains("ORDER BY MAX(t.StartedAt) DESC", sql);
        Assert.Equal(51, parameters.Get<int>("LimitPlusOne"));
    }

    [Theory]
    [InlineData(2, false)]   // Standard
    [InlineData(3, true)]    // Enterprise / Developer
    [InlineData(4, false)]   // Express
    [InlineData(5, true)]    // Azure SQL Database
    [InlineData(8, true)]    // Managed Instance
    public void Online_index_builds_only_where_the_edition_supports_them(int engineEdition, bool expected)
    {
        // Arrange

        // Act
        var online = FlowOrchestratorSqlMigrator.SupportsOnlineIndexBuild(engineEdition);

        // Assert
        Assert.Equal(expected, online);
    }

    [Fact]
    public void Index_ddl_is_guarded_and_online_only_when_asked()
    {
        // Arrange

        // Act
        var online = FlowOrchestratorSqlMigrator.BuildRunSearchIndexesSql(online: true);
        var offline = FlowOrchestratorSqlMigrator.BuildRunSearchIndexesSql(online: false);

        // Assert
        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FlowRuns_IdText'", online);
        Assert.Contains("INCLUDE ([RunSearchText], [FlowId], [Status]) WITH (ONLINE = ON);", online);
        Assert.DoesNotContain("ONLINE", offline);
    }

    [Theory]
    [InlineData(3, 15, false)]   // Enterprise / Developer 2019: CREATE INDEX has no WAIT_AT_LOW_PRIORITY
    [InlineData(3, 16, true)]    // Enterprise / Developer 2022
    [InlineData(3, 17, true)]    // Enterprise / Developer 2025
    [InlineData(5, 12, true)]    // Azure SQL Database reports major version 12
    [InlineData(8, 16, true)]    // Managed Instance
    [InlineData(2, 16, false)]   // Standard: no online build at all
    public void Low_priority_online_builds_only_where_CREATE_INDEX_supports_them(int engineEdition, int majorVersion, bool expected)
    {
        // Arrange

        // Act
        var lowPriority = FlowOrchestratorSqlMigrator.SupportsLowPriorityIndexBuild(engineEdition, majorVersion);

        // Assert
        Assert.Equal(expected, lowPriority);
    }

    [Fact]
    public void Low_priority_index_ddl_waits_without_blocking_and_gives_up_on_itself()
    {
        // Arrange

        // Act
        var lowPriority = FlowOrchestratorSqlMigrator.BuildRunSearchIndexesSql(online: true, lowPriority: true);

        // Assert
        Assert.Contains(
            $"WITH (ONLINE = ON (WAIT_AT_LOW_PRIORITY (MAX_DURATION = {FlowOrchestratorSqlMigrator.LowPriorityMaxDurationMinutes} MINUTES, ABORT_AFTER_WAIT = SELF)));",
            lowPriority);
        Assert.Equal(2, lowPriority.Split("WAIT_AT_LOW_PRIORITY").Length - 1);
    }

    [Fact]
    public void UseRunSearchIndex_makes_the_store_use_the_built_in_token_index()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = new FlowOrchestratorBuilder(services).UseSqlServer(ConnectionString).UseRunSearchIndex();
        services.AddSingleton(builder.RunSearchIndex);   // what AddFlowOrchestrator does
        using var sp = services.BuildServiceProvider();

        // Act
        var store = sp.GetRequiredService<SqlFlowRunStore>();
        var index = sp.GetRequiredService<SqlServerRunSearchTokenIndex>();

        // Assert
        Assert.True(builder.RunSearchIndex.Enabled);
        Assert.Same(builder.RunSearchIndex, index.Options);
        Assert.NotNull(store);
        Assert.Contains(sp.GetServices<IHostedService>(), s => s is SqlRunSearchIndexBackfillService);
    }

    [Fact]
    public void Backfill_service_is_registered_after_the_migrator()
    {
        // Arrange — hosted services start in registration order; the token tables must exist first.
        var services = new ServiceCollection();
        services.AddLogging();
        new FlowOrchestratorBuilder(services).UseSqlServer(ConnectionString);
        using var sp = services.BuildServiceProvider();

        // Act
        var hosted = sp.GetServices<IHostedService>().ToList();

        // Assert
        var migrator = hosted.FindIndex(s => s is FlowOrchestratorSqlMigrator);
        var backfill = hosted.FindIndex(s => s is SqlRunSearchIndexBackfillService);
        Assert.InRange(migrator, 0, backfill - 1);
    }
}
