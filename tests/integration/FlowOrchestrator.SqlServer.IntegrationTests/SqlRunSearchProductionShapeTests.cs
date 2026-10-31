#if NET10_0
// The query shape does not depend on the target framework, and seeding ~180 MB of step payload once
// per TFM would triple the cost for no extra signal — so this suite runs on net10.0 only.
using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MsSql;
using Xunit.Abstractions;

namespace FlowOrchestrator.SqlServer.Tests;

/// <summary>
/// Seeds the production shape behind the "run search takes ~2 minutes" report (insurance-integration,
/// 1.33.0, Azure SQL, measured 2026-10-27) and pins the I/O of the three searches users run:
/// a status word that is not a status (<c>Completed</c>), a full run id, and a TriggerKey fragment.
/// </summary>
/// <remarks>
/// <para>Assertions are on <c>SET STATISTICS IO</c> logical and LOB reads, not on wall-clock: reads are
/// deterministic for a given plan and data set, while an elapsed-time upper bound is the classic source
/// of CI flakes (CLAUDE.md, anti-flakiness rules). Elapsed time is measured and written to the test
/// output for the benchmark record only.</para>
/// <para>The legacy query text is reproduced verbatim from the 1.33.0 store so the same container
/// reports before/after numbers side by side.</para>
/// </remarks>
public sealed class SqlRunSearchProductionShapeTests : IClassFixture<SqlRunSearchProductionShapeTests.ProductionShapeFixture>
{
    private readonly ProductionShapeFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SqlRunSearchProductionShapeTests(ProductionShapeFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task Completed_maps_to_the_status_filter_and_never_reads_step_payloads()
    {
        // Arrange — what the dashboard sends for "Completed": RunSearchTerm maps it to Succeeded and
        // drops the text, so the search keeps an exact total.
        var query = new RunSearchQuery { Status = "Succeeded", Take = 50, IncludeTotal = true };

        // Act
        var (io, rows, total, elapsed) = await MeasureAsync(query);
        var legacy = await MeasureLegacyAsync("Completed");
        Report("Completed", query, io, elapsed, legacy);

        // Assert
        Assert.Equal(50, rows);
        Assert.Equal(_fixture.SucceededCount, total);
        Assert.DoesNotContain("FlowSteps", io.Tables);
        Assert.Equal(0, io.LobLogicalReads);
        Assert.InRange(io.LogicalReads, 1, 1_000);
        Assert.True(legacy.Io.LobLogicalReads > 10_000, "the legacy query is expected to read every step payload");
    }

    [Fact]
    public async Task Full_run_id_is_a_clustered_key_seek()
    {
        // Arrange
        var query = new RunSearchQuery { RunId = _fixture.OldRunId, Take = 50, IncludeTotal = true };

        // Act
        var (io, rows, total, elapsed) = await MeasureAsync(query);
        var legacy = await MeasureLegacyAsync(_fixture.OldRunId.ToString("D"));
        Report("full run id", query, io, elapsed, legacy);

        // Assert — the run is 50 days old: outside any default window, still found by id.
        Assert.Equal(1, rows);
        Assert.Null(total);
        Assert.DoesNotContain("FlowSteps", io.Tables);
        Assert.Equal(0, io.LobLogicalReads);
        Assert.InRange(io.LogicalReads, 1, 20);
    }

    [Fact]
    public async Task TriggerKey_fragment_reads_only_the_default_window_of_run_columns()
    {
        // Arrange — a rare fragment, so the scan cannot stop early: the worst case for a windowed search.
        var query = new RunSearchQuery
        {
            Text = ProductionShapeFixture.RareTriggerFragment,
            StartedFrom = _fixture.Now.AddDays(-7),
            Take = 50,
            IncludeTotal = true,
        };

        // Act
        var (io, rows, total, elapsed) = await MeasureAsync(query);
        var legacy = await MeasureLegacyAsync(ProductionShapeFixture.RareTriggerFragment);
        Report("TriggerKey fragment", query, io, elapsed, legacy);

        // Assert
        Assert.Equal(1, rows);
        Assert.Null(total);
        Assert.DoesNotContain("FlowSteps", io.Tables);
        Assert.Equal(0, io.LobLogicalReads);
        Assert.InRange(io.LogicalReads, 1, 1_000);
    }

    [Fact]
    public async Task Opt_in_step_payload_search_reads_payloads_of_the_window_only()
    {
        // Arrange
        var query = new RunSearchQuery
        {
            Text = ProductionShapeFixture.PayloadToken,
            IncludeStepPayloads = true,
            StartedFrom = _fixture.Now.AddDays(-7),
            Take = 50,
            IncludeTotal = true,
        };

        // Act
        var (io, rows, _, elapsed) = await MeasureAsync(query);
        var legacy = await MeasureLegacyAsync(ProductionShapeFixture.PayloadToken);
        Report("step payload (opt-in)", query, io, elapsed, legacy);

        // Assert — it does read payloads (that is the point), but ~7/60 of them.
        Assert.Equal(1, rows);
        Assert.Contains("FlowSteps", io.Tables);
        Assert.True(io.LobLogicalReads > 0);
        Assert.True(io.LobLogicalReads * 4 < legacy.Io.LobLogicalReads,
            $"windowed payload search read {io.LobLogicalReads} LOB pages, legacy read {legacy.Io.LobLogicalReads}");
    }

    private async Task<(IoStats Io, int Rows, int? Total, TimeSpan Elapsed)> MeasureAsync(RunSearchQuery query)
    {
        var command = SqlFlowRunStore.BuildSearchRunsCommand(query);
        // Warm the plan cache and buffer pool so the timing is the steady state, then measure.
        await RunAsync(command.Sql, command.Parameters, command.CountsTotal);
        var (io, rows, total, elapsed) = await RunAsync(command.Sql, command.Parameters, command.CountsTotal);

        // The public store path must agree with the SQL measured here.
        var page = await new SqlFlowRunStore(_fixture.ConnectionString).SearchRunsAsync(query);
        Assert.Equal(Math.Min(rows, query.Take), page.Runs.Count);
        Assert.Equal(total, page.TotalCount);
        return (io, Math.Min(rows, query.Take), total, elapsed);
    }

    private async Task<(IoStats Io, int Rows, int? Total, TimeSpan Elapsed)> MeasureLegacyAsync(string term)
    {
        // 1.33.0 SqlFlowRunStore.GetRunsPageAsync(deepSearch: true), the dashboard default, unwindowed.
        const string legacySql = """
            SELECT fr.Id, fr.FlowId, fr.FlowName, fr.Status, fr.TriggerKey, fr.TriggerDataJson,
                   fr.BackgroundJobId, fr.StartedAt, fr.CompletedAt, fr.SourceRunId,
                   COUNT(*) OVER() AS TotalCount
            FROM FlowRuns AS fr
            WHERE ( CAST(fr.Id AS NVARCHAR(36)) LIKE @SearchLike ESCAPE '\'
                 OR ISNULL(fr.FlowName, '') LIKE @SearchLike ESCAPE '\'
                 OR ISNULL(fr.TriggerKey, '') LIKE @SearchLike ESCAPE '\'
                 OR ISNULL(fr.Status, '') LIKE @SearchLike ESCAPE '\'
                 OR ISNULL(fr.BackgroundJobId, '') LIKE @SearchLike ESCAPE '\'
                 OR EXISTS (SELECT 1 FROM FlowSteps AS fs
                            WHERE fs.RunId = fr.Id
                              AND ( ISNULL(fs.StepKey, '') LIKE @SearchLike ESCAPE '\'
                                 OR ISNULL(fs.ErrorMessage, '') LIKE @SearchLike ESCAPE '\'
                                 OR ISNULL(fs.OutputJson, '') LIKE @SearchLike ESCAPE '\' )) )
            ORDER BY fr.StartedAt DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
            """;
        var parameters = new { SearchLike = "%" + term + "%", Skip = 0, Take = 50 };
        await RunAsync(legacySql, parameters, countsTotal: false);
        return await RunAsync(legacySql, parameters, countsTotal: false);
    }

    private async Task<(IoStats Io, int Rows, int? Total, TimeSpan Elapsed)> RunAsync(string sql, object parameters, bool countsTotal)
    {
        var messages = new List<string>();
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        conn.InfoMessage += (_, e) => { lock (messages) messages.Add(e.Message); };
        await conn.OpenAsync();
        await conn.ExecuteAsync("SET STATISTICS IO ON;");

        var sw = Stopwatch.StartNew();
        int rows;
        int? total;
        using (var grid = await conn.QueryMultipleAsync(new CommandDefinition(sql, parameters, commandTimeout: 600)))
        {
            rows = (await grid.ReadAsync()).Count();
            total = countsTotal ? await grid.ReadSingleAsync<int>() : null;
            sw.Stop();
        }

        // A second round trip drains every STATISTICS IO message of the measured batch.
        await conn.ExecuteAsync("SET STATISTICS IO OFF;");
        lock (messages) return (IoStats.Parse(messages), rows, total, sw.Elapsed);
    }

    private void Report(string name, RunSearchQuery query, IoStats io, TimeSpan elapsed, (IoStats Io, int Rows, int? Total, TimeSpan Elapsed) legacy)
    {
        var sql = SqlFlowRunStore.BuildSearchRunsCommand(query).Sql;
        _output.WriteLine($"### {name}");
        _output.WriteLine($"before: {legacy.Elapsed.TotalMilliseconds,9:F1} ms  logical {legacy.Io.LogicalReads,8}  lob {legacy.Io.LobLogicalReads,8}  tables {string.Join(",", legacy.Io.Tables)}");
        _output.WriteLine($"after:  {elapsed.TotalMilliseconds,9:F1} ms  logical {io.LogicalReads,8}  lob {io.LobLogicalReads,8}  tables {string.Join(",", io.Tables)}");
        _output.WriteLine("SQL:");
        _output.WriteLine(sql);
        _output.WriteLine(string.Empty);
    }

    /// <summary>Totals of one statement batch's <c>SET STATISTICS IO</c> messages.</summary>
    private sealed record IoStats(long LogicalReads, long LobLogicalReads, IReadOnlyList<string> Tables)
    {
        private static readonly Regex Line = new(
            @"Table '(?<table>[^']+)'\. Scan count \d+, logical reads (?<logical>\d+),.*?lob logical reads (?<lob>\d+)",
            RegexOptions.Compiled);

        public static IoStats Parse(IEnumerable<string> messages)
        {
            long logical = 0, lob = 0;
            var tables = new List<string>();
            foreach (var message in messages)
            {
                foreach (Match m in Line.Matches(message))
                {
                    var table = m.Groups["table"].Value;
                    if (!tables.Contains(table)) tables.Add(table);
                    logical += long.Parse(m.Groups["logical"].Value);
                    lob += long.Parse(m.Groups["lob"].Value);
                }
            }
            return new IoStats(logical, lob, tables);
        }
    }

    /// <summary>
    /// One SQL Server container holding the production shape: 15,000 runs over 60 days (73% Skipped /
    /// 25% Succeeded / 2% Failed), three steps each, ~12 KB of <c>OutputJson</c> on the first step.
    /// </summary>
    public sealed class ProductionShapeFixture : IAsyncLifetime
    {
        public const int RunCount = 15_000;
        public const string RareTriggerFragment = "claim-0001";
        public const string PayloadToken = "policy-77f3c2e1-needle";

        private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

        public string ConnectionString => _container.GetConnectionString();
        public DateTimeOffset Now { get; } = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddHours(12);
        public Guid OldRunId { get; private set; }
        public int SucceededCount { get; private set; }

        public async Task InitializeAsync()
        {
            await SqlServerStartHelper.StartWithRetryAsync(_container);
            await new FlowOrchestratorSqlMigrator(ConnectionString, NullLogger<FlowOrchestratorSqlMigrator>.Instance)
                .StartAsync(CancellationToken.None);
            await SeedAsync();
        }

        public Task DisposeAsync() => _container.DisposeAsync().AsTask();

        private async Task SeedAsync()
        {
            // ~6,000 UTF-16 chars = ~12 KB per fetchJobDetails payload: above the 8,000-byte in-row
            // limit, so it lives on LOB pages exactly as in production.
            var payload = BuildPayload(6_000, "job");
            var needlePayload = BuildPayload(6_000, PayloadToken);
            var flowId = Guid.NewGuid();
            var span = TimeSpan.FromDays(60);

            var runs = new DataTable();
            runs.Columns.Add("Id", typeof(Guid));
            runs.Columns.Add("FlowId", typeof(Guid));
            runs.Columns.Add("FlowName", typeof(string));
            runs.Columns.Add("Status", typeof(string));
            runs.Columns.Add("TriggerKey", typeof(string));
            runs.Columns.Add("BackgroundJobId", typeof(string));
            runs.Columns.Add("StartedAt", typeof(DateTimeOffset));
            runs.Columns.Add("CompletedAt", typeof(DateTimeOffset));

            var steps = new DataTable();
            steps.Columns.Add("RunId", typeof(Guid));
            steps.Columns.Add("StepKey", typeof(string));
            steps.Columns.Add("StepType", typeof(string));
            steps.Columns.Add("Status", typeof(string));
            steps.Columns.Add("OutputJson", typeof(string));
            steps.Columns.Add("ErrorMessage", typeof(string));
            steps.Columns.Add("StartedAt", typeof(DateTimeOffset));
            steps.Columns.Add("CompletedAt", typeof(DateTimeOffset));

            for (var i = 0; i < RunCount; i++)
            {
                var id = Guid.NewGuid();
                var startedAt = Now - TimeSpan.FromTicks(span.Ticks / RunCount * i);
                var bucket = i % 100;
                var status = bucket < 73 ? "Skipped" : bucket < 98 ? "Succeeded" : "Failed";
                if (status == "Succeeded") SucceededCount++;
                // Every 10th run is webhook-triggered; "claim-0001" then matches run 10 only.
                var triggerKey = i % 10 == 0 ? $"webhook-claim-{i:D5}" : "schedule";
                if (i == RunCount * 50 / 60) OldRunId = id;

                runs.Rows.Add(id, flowId, "InsuranceJobSyncFlow", status, triggerKey, $"hf-{i}", startedAt, startedAt.AddSeconds(4));
                steps.Rows.Add(id, "fetchJobDetails", "HttpFetch", status == "Failed" ? "Failed" : "Succeeded",
                    i == 25 ? needlePayload : payload,
                    status == "Failed" ? "Upstream returned 503 Service Unavailable" : null, startedAt, startedAt.AddSeconds(2));
                steps.Rows.Add(id, "transform", "Transform", status == "Succeeded" ? "Succeeded" : "Skipped",
                    "{\"mapped\":true}", null, startedAt.AddSeconds(2), startedAt.AddSeconds(3));
                steps.Rows.Add(id, "notify", "Notify", status == "Succeeded" ? "Succeeded" : "Skipped",
                    null, null, startedAt.AddSeconds(3), startedAt.AddSeconds(4));
            }

            await using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            await BulkCopyAsync(conn, "FlowRuns", runs);
            await BulkCopyAsync(conn, "FlowSteps", steps);
            await conn.ExecuteAsync("UPDATE STATISTICS FlowRuns WITH FULLSCAN; UPDATE STATISTICS FlowSteps WITH FULLSCAN;", commandTimeout: 600);
        }

        private static async Task BulkCopyAsync(SqlConnection conn, string table, DataTable data)
        {
            using var bulk = new SqlBulkCopy(conn) { DestinationTableName = table, BatchSize = 2_000, BulkCopyTimeout = 600 };
            foreach (DataColumn column in data.Columns)
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            await bulk.WriteToServerAsync(data);
        }

        private static string BuildPayload(int length, string marker)
        {
            var sb = new StringBuilder(length + 64);
            sb.Append("{\"marker\":\"").Append(marker).Append("\",\"jobDetails\":[");
            var n = 0;
            while (sb.Length < length)
                sb.Append("{\"line\":").Append(n++).Append(",\"code\":\"INS-PRM-001\",\"amount\":1250.75},");
            sb.Append("{}]}");
            return sb.ToString();
        }
    }
}
#endif
