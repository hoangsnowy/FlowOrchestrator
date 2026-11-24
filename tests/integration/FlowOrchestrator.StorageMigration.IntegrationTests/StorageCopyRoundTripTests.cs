using Dapper;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowOrchestrator.StorageMigration.IntegrationTests;

/// <summary>
/// The whole promise of the tool, in both directions: seed a source by running real flows, copy it, and every
/// fact a host reads through the store APIs reads back equal on the target — then a 2.0 engine on the target
/// starts, runs new flows, dedupes on copied idempotency keys and finds copied runs in search.
/// </summary>
public sealed class StorageCopyRoundTripTests
{
    private readonly DatabaseFixture _db;

    public StorageCopyRoundTripTests(DatabaseFixture db) => _db = db;

    public static TheoryData<FlowStorageProvider, FlowStorageProvider> Directions() => new()
    {
        { FlowStorageProvider.SqlServer, FlowStorageProvider.PostgreSql },
        { FlowStorageProvider.PostgreSql, FlowStorageProvider.SqlServer },
    };

    [Theory]
    [MemberData(nameof(Directions))]
    public async Task A_seeded_database_reads_back_equal_on_the_other_backend_and_keeps_working(FlowStorageProvider source, FlowStorageProvider target)
    {
        // Arrange
        var sourceCs = await _db.CreateDatabaseAsync(source);
        var targetCs = await _db.CreateDatabaseAsync(target);
        SeedInfo seed;
        await using (var host = await FlowHost.StartAsync(source, sourceCs))
            seed = await Seeder.SeedAsync(host);
        SortedDictionary<string, string> expected;
        await using (var reader = StoreReader.Create(source, sourceCs))
            expected = await StoreSnapshot.CaptureAsync(reader, seed);
        if (target == FlowStorageProvider.SqlServer)
            await PrepareSqlTargetWithCompletedTokenIndexAsync(targetCs);
        var progress = new RecordingProgress();
        var copier = new FlowStorageCopier(new FlowStorageCopyOptions
        {
            SourceProvider = source, SourceConnectionString = sourceCs,
            TargetProvider = target, TargetConnectionString = targetCs,
            BatchSize = 7, // several keyset batches per table, including composite keys
        });

        // Act
        var result = await copier.CopyAsync(progress);

        // Assert — the tool's own verification.
        Assert.True(result.IsVerified);
        Assert.All(result.Tables, t => Assert.Equal(t.SourceRows, t.TargetRows));
        Assert.Contains(result.Tables, t => t.Table == "FlowSteps" && t.CopiedRows > 14);
        Assert.Equal(1, result.Tables.Single(t => t.Table == "WebhookReplayNonces").CopiedRows);
        Assert.Equal(3, result.Tables.Single(t => t.Table == "WebhookRejections").CopiedRows);
        Assert.Contains(progress.Reports, p => p.Phase == FlowStorageCopyPhase.Verify && p.Table == "FlowRuns");

        // Assert — every fact, read through the target's store APIs, equals the source's.
        Dictionary<string, string> actual;
        await using (var reader = StoreReader.Create(target, targetCs))
        {
            actual = new(await StoreSnapshot.CaptureAsync(reader, seed));
            Assert.False(await reader.Replay.TryRegisterAsync(seed.ValidNonceFlow, "manual", SeedInfo.ValidNonce, DateTimeOffset.UtcNow.AddHours(1)));
            Assert.True(await reader.Replay.TryRegisterAsync(seed.ValidNonceFlow, "manual", SeedInfo.ExpiredNonce, DateTimeOffset.UtcNow.AddHours(1)));
        }

        Assert.True(expected.Count >= 50, $"Only {expected.Count} facts were captured; the comparison would be vacuous.");
        Assert.Equal(expected.Keys, actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (fact, value) in expected)
            Assert.True(value == actual[fact], $"{fact} differs.\nsource: {Trim(value)}\ntarget: {Trim(actual[fact])}");
        Assert.Contains("\"Status\":\"Cancelled\"", expected[$"run.{seed.CancelledRunId}"]);
        Assert.Contains("\"Status\":\"Failed\"", expected[$"run.{seed.FailedRunId}"]);
        Assert.Contains("\"Status\":\"Skipped\"", expected[$"run.{seed.SmallOrderRunId}"]);
        Assert.Contains("\"IsPaused\":true", expected["schedules"]);
        Assert.Contains("\"IsEnabled\":false", expected["flows"]);
        Assert.Contains("\"Attempt\":2", expected[$"run.{seed.FailedRunId}"]);
        Assert.DoesNotContain("null", expected[$"idempotency.{new OrderFlow().Id}.order-42-✓"]);

        if (target == FlowStorageProvider.SqlServer)
        {
            Assert.Contains(result.Notes, n => n.Contains("run-search token index was reset", StringComparison.Ordinal));
            await using var conn = new SqlConnection(targetCs);
            Assert.Null(await conn.ExecuteScalarAsync<DateTimeOffset?>("SELECT BackfillCompletedAt FROM FlowRunSearchIndexState WHERE Id = 1"));
        }

        // Assert — a 2.0 host on the target starts, runs, dedupes and searches the copied history.
        await using var targetHost = await FlowHost.StartAsync(target, targetCs);
        var (newRun, newStatus) = await targetHost.RunAsync<OrderFlow>(new { label = "after the move", amount = 2000, items = new[] { "x" } });
        Assert.Equal("Succeeded", newStatus);
        Assert.Equal(seed.BigOrderRunId, await targetHost.TriggerAsync<OrderFlow>(new { }, new Dictionary<string, string> { ["Idempotency-Key"] = "order-42-✓" }));

        var runs = targetHost.Services.GetRequiredService<IFlowRunStore>();
        var deep = await runs.SearchRunsAsync(new RunSearchQuery { Text = seed.DeepSearchNeedle, IncludeStepPayloads = true, Take = 10 });
        Assert.Equal([seed.BigOrderRunId], deep.Runs.Select(r => r.Id));
        var byFlow = await runs.SearchRunsAsync(new RunSearchQuery { Text = nameof(FailingFlow), Take = 10 });
        Assert.Equal([seed.FailedRunId], byFlow.Runs.Select(r => r.Id));
        var byId = await runs.SearchRunsAsync(new RunSearchQuery { Text = seed.CancelledRunId.ToString()[..13], Take = 10 });
        Assert.Contains(seed.CancelledRunId, byId.Runs.Select(r => r.Id));
        Assert.NotEqual(Guid.Empty, newRun);

        // The identity / sequence moved past the copied ids: a new log row gets a fresh id.
        var rejections = targetHost.Services.GetRequiredService<IWebhookRejectionStore>();
        await rejections.WriteAsync(new WebhookRejectionRecord { ReceivedAt = DateTimeOffset.UtcNow, Reason = "after_move", StatusCode = 400 });
        var log = await rejections.QueryRecentAsync(null, null, true, 0, 100);
        Assert.Equal(4, log.Count);
        Assert.Equal(4, log.Select(r => r.Id).Distinct().Count());
    }

    /// <summary>
    /// A target that a host already migrated with <c>UseRunSearchIndex()</c>, whose (empty) back-fill already
    /// completed — the case where copied runs would never be token-indexed unless the copy resets the state.
    /// </summary>
    private static async Task PrepareSqlTargetWithCompletedTokenIndexAsync(string targetCs)
    {
        using var migrator = new FlowOrchestratorSqlMigrator(targetCs, NullLogger<FlowOrchestratorSqlMigrator>.Instance, runSearchIndexEnabled: true);
        await migrator.StartAsync(CancellationToken.None);
        await migrator.BackgroundIndexBuild;
        await using var conn = new SqlConnection(targetCs);
        await conn.ExecuteAsync("UPDATE FlowRunSearchIndexState SET BackfillCompletedAt = SYSDATETIMEOFFSET() WHERE Id = 1");
    }

    private static string Trim(string value) => value.Length <= 600 ? value : value[..600] + "…";
}

/// <summary>A synchronous <see cref="IProgress{T}"/> (the BCL one posts to the thread pool, out of order).</summary>
public sealed class RecordingProgress : IProgress<FlowStorageCopyProgress>
{
    private readonly Lock _gate = new();
    private readonly List<FlowStorageCopyProgress> _reports = [];

    public IReadOnlyList<FlowStorageCopyProgress> Reports
    {
        get
        {
            lock (_gate)
                return _reports.ToArray();
        }
    }

    public void Report(FlowStorageCopyProgress value)
    {
        lock (_gate)
            _reports.Add(value);
    }
}
