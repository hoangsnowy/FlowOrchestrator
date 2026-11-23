using System.Buffers;
using System.Text.Json;
using Dapper;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.SqlServer;

/// <summary>
/// Back-fills existing runs into the SQL Server run-search token index, newest first, then sweeps tokens
/// of runs deleted by retention every <see cref="RunSearchIndexOptions.OrphanSweepInterval"/>.
/// </summary>
/// <remarks>
/// <para><b>Resumable.</b> Progress is a keyset cursor <c>(StartedAt, Id)</c> in
/// <c>FlowRunSearchIndexState</c>, written in the same transaction as each batch's tokens; a restart
/// continues after the last committed batch. Re-indexing a run is harmless (<c>IGNORE_DUP_KEY</c>).</para>
/// <para><b>Newest first</b>, so the runs people search for become fast first. Until the back-fill
/// completes, deep search keeps the <c>LIKE</c> probe for runs at or before the cursor — results stay
/// complete while the index fills.</para>
/// <para><b>One replica at a time.</b> The service holds a session-scoped <c>sp_getapplock</c> while it
/// works; other replicas retry later.</para>
/// <para>Cost: one transaction per <see cref="RunSearchIndexOptions.BackfillBatchSize"/> runs, reading
/// those runs' trigger bodies and current step rows (payloads included). Not on any request or step path.</para>
/// </remarks>
internal sealed partial class SqlRunSearchIndexBackfillService : BackgroundService
{
    private const string AppLockResource = "FlowOrchestrator.RunSearchIndex.Backfill";
    private static readonly TimeSpan LockRetryInterval = TimeSpan.FromMinutes(1);
    private const int ProgressEveryBatches = 25;

    private readonly string _connectionString;
    private readonly SqlServerRunSearchTokenIndex _index;
    private readonly RunSearchIndexOptions _options;
    private readonly ILogger<SqlRunSearchIndexBackfillService> _logger;
    private readonly TimeProvider _time;
    private readonly TaskCompletionSource _backfillPass = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SqlRunSearchIndexBackfillService(
        string connectionString,
        SqlServerRunSearchTokenIndex index,
        ILogger<SqlRunSearchIndexBackfillService> logger,
        TimeProvider? time = null)
    {
        _connectionString = connectionString;
        _index = index;
        _options = index.Options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Completes when this instance's back-fill pass has ended (completed, already complete, or disabled); test seam.</summary>
    internal Task BackfillPass => _backfillPass.Task;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _backfillPass.TrySetResult();
            return;
        }

        try
        {
            if (_options.Backfill)
                await BackfillUntilCompleteAsync(stoppingToken).ConfigureAwait(false);
            _backfillPass.TrySetResult();

            var interval = _options.OrphanSweepInterval > TimeSpan.Zero ? _options.OrphanSweepInterval : TimeSpan.FromHours(24);
            using var timer = new PeriodicTimer(interval, _time);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var deleted = await _index.SweepOrphansAsync(stoppingToken).ConfigureAwait(false);
                LogOrphansSwept(deleted);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown: the cursor of the last committed batch is the resume point.
            _backfillPass.TrySetCanceled(stoppingToken);
        }
        catch (SqlException ex)
        {
            // Never take the host down for a search index; deep search falls back to LIKE meanwhile.
            LogBackfillFailed(ex);
            _backfillPass.TrySetException(ex);
        }
    }

    private async Task BackfillUntilCompleteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var lockConn = new SqlConnection(_connectionString);
            await lockConn.OpenAsync(stoppingToken).ConfigureAwait(false);
            var state = await ReadStateAsync(lockConn, stoppingToken).ConfigureAwait(false);
            if (state.CompletedAt is not null)
                return;

            var acquired = await lockConn.ExecuteScalarAsync<int>(new CommandDefinition(
                "DECLARE @r INT; EXEC @r = sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;",
                new { Resource = AppLockResource }, cancellationToken: stoppingToken)).ConfigureAwait(false);
            if (acquired < 0)
            {
                LogBackfillLockBusy();
                await Task.Delay(LockRetryInterval, _time, stoppingToken).ConfigureAwait(false);
                continue;
            }

            // The lock lives as long as lockConn; batches run on their own connections.
            await RunBatchesAsync(state, stoppingToken).ConfigureAwait(false);
            return;
        }
    }

    private async Task RunBatchesAsync(BackfillState state, CancellationToken stoppingToken)
    {
        var batchSize = Math.Clamp(_options.BackfillBatchSize, 1, 5_000);
        var cursor = state.AfterStartedAt is { } s && state.AfterId is { } id ? new RunSearchCursor(s, id) : (RunSearchCursor?)null;
        long runs = 0;
        var batches = 0;
        LogBackfillStarted(cursor?.StartedAt);

        while (!stoppingToken.IsCancellationRequested)
        {
            var batch = await IndexBatchAsync(cursor, batchSize, stoppingToken).ConfigureAwait(false);
            if (batch.Runs == 0)
            {
                await using var conn = new SqlConnection(_connectionString);
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE FlowRunSearchIndexState SET BackfillCompletedAt = SYSDATETIMEOFFSET() WHERE Id = 1;",
                    cancellationToken: stoppingToken)).ConfigureAwait(false);
                LogBackfillCompleted(runs);
                return;
            }

            runs += batch.Runs;
            cursor = batch.Last;
            if (++batches % ProgressEveryBatches == 0)
                LogBackfillProgress(runs, cursor!.Value.StartedAt);
            if (_options.BackfillBatchDelay > TimeSpan.Zero)
                await Task.Delay(_options.BackfillBatchDelay, _time, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task<(int Runs, int Tokens, RunSearchCursor? Last)> IndexBatchAsync(RunSearchCursor? cursor, int batchSize, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var runs = (await conn.QueryAsync<(Guid Id, DateTimeOffset StartedAt, string? TriggerDataJson)>(new CommandDefinition(
            cursor is null
                ? "SELECT TOP (@Batch) Id, StartedAt, TriggerDataJson FROM FlowRuns ORDER BY StartedAt DESC, Id;"
                : "SELECT TOP (@Batch) Id, StartedAt, TriggerDataJson FROM FlowRuns WHERE StartedAt < @S OR (StartedAt = @S AND Id > @Id) ORDER BY StartedAt DESC, Id;",
            new { Batch = batchSize, S = cursor?.StartedAt, Id = cursor?.Id },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
        if (runs.Count == 0)
            return (0, 0, cursor);

        var ids = new Guid[runs.Count];
        for (var i = 0; i < runs.Count; i++) ids[i] = runs[i].Id;
        var steps = await conn.QueryAsync<(Guid RunId, string StepKey, string? ErrorMessage, string? OutputJson)>(new CommandDefinition(
            "SELECT RunId, StepKey, ErrorMessage, OutputJson FROM FlowSteps WHERE RunId IN @Ids;",
            new { Ids = ids }, commandTimeout: 300, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var startedAtById = new Dictionary<Guid, DateTimeOffset>(runs.Count);
        foreach (var run in runs) startedAtById[run.Id] = run.StartedAt;

        // One JSON array for the whole batch: [{t, s, r, a}] inserted by a single OPENJSON statement.
        var buffer = new ArrayBufferWriter<byte>(64 * 1024);
        var tokens = 0;
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            var set = new HashSet<string>(StringComparer.Ordinal);
            var max = _options.MaxTokensPerDocument > 0 ? _options.MaxTokensPerDocument : RunSearchTokenizer.DefaultMaxTokensPerDocument;
            foreach (var run in runs)
            {
                set.Clear();
                RunSearchTokenizer.AddJsonValues(run.TriggerDataJson, set, max);
                tokens += Write(writer, set, RunSearchTokenSource.Trigger, run.Id, run.StartedAt);
            }

            foreach (var step in steps)
            {
                if (!startedAtById.TryGetValue(step.RunId, out var startedAt))
                    continue;
                set.Clear();
                RunSearchTokenizer.AddText(step.StepKey, set, camelCase: true, max);
                tokens += Write(writer, set, RunSearchTokenSource.StepKey, step.RunId, startedAt);
                set.Clear();
                RunSearchTokenizer.AddText(step.ErrorMessage, set, camelCase: false, max);
                tokens += Write(writer, set, RunSearchTokenSource.Error, step.RunId, startedAt);
                set.Clear();
                RunSearchTokenizer.AddJsonValues(step.OutputJson, set, max);
                tokens += Write(writer, set, RunSearchTokenSource.Payload, step.RunId, startedAt);
            }

            writer.WriteEndArray();
        }

        var last = runs[^1];
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO FlowRunSearchTokens (Token, StartedAt, RunId, Source)
            SELECT j.t, j.a, j.r, j.s
            FROM OPENJSON(@Tokens) WITH (t NVARCHAR(64) '$.t', s TINYINT '$.s', r UNIQUEIDENTIFIER '$.r', a DATETIMEOFFSET '$.a') AS j
            ORDER BY j.t, j.a DESC, j.r, j.s;
            UPDATE FlowRunSearchIndexState SET BackfillAfterStartedAt = @S, BackfillAfterId = @Id WHERE Id = 1;
            """,
            new { Tokens = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan), S = last.StartedAt, last.Id },
            tx, commandTimeout: 300, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (runs.Count, tokens, new RunSearchCursor(last.StartedAt, last.Id));
    }

    private static int Write(Utf8JsonWriter writer, HashSet<string> tokens, RunSearchTokenSource source, Guid runId, DateTimeOffset startedAt)
    {
        foreach (var token in tokens)
        {
            writer.WriteStartObject();
            writer.WriteString("t"u8, token);
            writer.WriteNumber("s"u8, (byte)source);
            writer.WriteString("r"u8, runId);
            writer.WriteString("a"u8, startedAt);
            writer.WriteEndObject();
        }

        return tokens.Count;
    }

    private static async Task<BackfillState> ReadStateAsync(SqlConnection conn, CancellationToken cancellationToken)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(DateTimeOffset? AfterStartedAt, Guid? AfterId, DateTimeOffset? CompletedAt)>(new CommandDefinition(
            "SELECT BackfillAfterStartedAt, BackfillAfterId, BackfillCompletedAt FROM FlowRunSearchIndexState WHERE Id = 1;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new BackfillState(row.AfterStartedAt, row.AfterId, row.CompletedAt);
    }

    private readonly record struct BackfillState(DateTimeOffset? AfterStartedAt, Guid? AfterId, DateTimeOffset? CompletedAt);

    [LoggerMessage(EventId = 7301, Level = LogLevel.Information, Message = "Run-search token back-fill started (resuming after {ResumeAfter}).")]
    private partial void LogBackfillStarted(DateTimeOffset? resumeAfter);

    [LoggerMessage(EventId = 7302, Level = LogLevel.Information, Message = "Run-search token back-fill: {Runs} runs indexed so far, now at runs started {StartedAt}.")]
    private partial void LogBackfillProgress(long runs, DateTimeOffset startedAt);

    [LoggerMessage(EventId = 7303, Level = LogLevel.Information, Message = "Run-search token back-fill completed: {Runs} runs indexed in this pass.")]
    private partial void LogBackfillCompleted(long runs);

    [LoggerMessage(EventId = 7304, Level = LogLevel.Debug, Message = "Run-search token back-fill is running on another instance; retrying later.")]
    private partial void LogBackfillLockBusy();

    [LoggerMessage(EventId = 7305, Level = LogLevel.Warning, Message = "Run-search token back-fill stopped; deep search keeps the LIKE fallback for runs not yet indexed. It resumes on the next start.")]
    private partial void LogBackfillFailed(Exception exception);

    [LoggerMessage(EventId = 7306, Level = LogLevel.Information, Message = "Run-search token sweep removed {Deleted} tokens of deleted runs.")]
    private partial void LogOrphansSwept(long deleted);
}
