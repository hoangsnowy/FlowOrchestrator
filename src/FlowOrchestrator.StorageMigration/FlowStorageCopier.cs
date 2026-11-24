using System.Diagnostics;
using System.Globalization;
using FlowOrchestrator.StorageMigration.Internal;
using FlowOrchestrator.StorageMigration.Schema;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FlowOrchestrator.StorageMigration;

/// <summary>
/// Copies a FlowOrchestrator database between SQL Server and PostgreSQL (either direction, or between two
/// databases of the same backend): flow definitions, run history, step attempts, outputs, run controls,
/// idempotency keys, the dispatch ledger and claims, schedule state, signal waiters and the webhook logs.
/// </summary>
/// <remarks>
/// <para><b>Order of work.</b> Preflight (schemas, in-flight runs, empty target) → target schema via the target
/// backend's own 2.0 migrator → each table copied in key order, in batches, inside its own target
/// transaction → verification (row count + order-independent content checksum per table, both sides).</para>
/// <para><b>Not copied.</b> Hangfire's own storage (drain the queues first; recurring cron jobs are re-registered
/// from the flow manifests and the copied schedule state when the host starts on the new storage), the SQL
/// Server run-search token index (re-built by <c>UseRunSearchIndex()</c>'s back-fill), and expired webhook
/// replay nonces.</para>
/// <para><b>Precision.</b> SQL Server keeps timestamps to 100 ns, PostgreSQL to 1 µs: SQL Server → PostgreSQL
/// truncates the last digit. Offsets are not kept by PostgreSQL either; every value is stored as the same
/// instant in UTC.</para>
/// <para>The source is only read (unless <see cref="FlowStorageCopyOptions.MigrateSourceSchema"/>), so it stays
/// a complete rollback point.</para>
/// </remarks>
public sealed partial class FlowStorageCopier
{
    private readonly FlowStorageCopyOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<FlowStorageCopier> _logger;

    /// <summary>Creates a copier for one run.</summary>
    /// <param name="options">Source, target and behaviour. Validated on each call.</param>
    public FlowStorageCopier(FlowStorageCopyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<FlowStorageCopier>();
    }

    /// <summary>
    /// Runs the preflight checks, creates the target schema, copies every table and verifies the result
    /// (or, with <see cref="FlowStorageCopyOptions.DryRun"/>, only checks and counts).
    /// </summary>
    /// <param name="progress">Receives a report per phase and per batch; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the run. The table being copied is rolled back; earlier tables stay.</param>
    /// <returns>Per-table counts and checksums.</returns>
    /// <exception cref="FlowStoragePreflightException">A preflight rule refused the copy; nothing was written.</exception>
    /// <exception cref="FlowStorageCopyException">A row cannot be stored on the target as it is.</exception>
    /// <exception cref="FlowStorageVerificationException">The target does not read back equal to the source.</exception>
    public async Task<FlowStorageCopyResult> CopyAsync(
        IProgress<FlowStorageCopyProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        _options.Validate();
        var clock = Stopwatch.StartNew();
        var cutoff = NonceCutoff();
        var notes = new List<string>();

        await using var source = CreateEndpoint(_options.SourceProvider, _options.SourceConnectionString);
        await using var target = CreateEndpoint(_options.TargetProvider, _options.TargetConnectionString);
        EnsureDistinct(source, target);

        Report(progress, FlowStorageCopyPhase.Preflight, null, 0, null, $"Reading the source schema ({source.Display}).");
        var sourceSchema = await source.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
        sourceSchema = await EnsureSourceSchemaAsync(source, sourceSchema, progress, notes, cancellationToken).ConfigureAwait(false);
        var tables = SelectTables(sourceSchema, notes);
        AddSourceNotes(sourceSchema, notes);

        Report(progress, FlowStorageCopyPhase.Preflight, null, 0, null, "Checking the source for in-flight runs.");
        await CheckActiveRunsAsync(source, notes, cancellationToken).ConfigureAwait(false);

        Report(progress, FlowStorageCopyPhase.Preflight, null, 0, null, $"Reading the target schema ({target.Display}).");
        var targetSchema = await target.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
        var schemaCreated = !HasCompleteSchema(targetSchema, _options.TargetProvider);
        RejectUnknownColumns(targetSchema, _options.TargetProvider, "target");
        var nonEmpty = await FindNonEmptyTablesAsync(target, targetSchema, tables, cancellationToken).ConfigureAwait(false);
        if (nonEmpty.Count > 0 && !_options.Resume)
        {
            throw new FlowStoragePreflightException(
                FlowStoragePreflightFailure.TargetNotEmpty,
                $"The target ({target.Display}) already contains FlowOrchestrator rows in: {string.Join(", ", nonEmpty)}. "
                + "Copy into an empty database: create a new one (the copy creates the schema), or drop the FlowOrchestrator tables. "
                + "Do not start a FlowOrchestrator host on the target before the copy - its start-up writes flow definitions. "
                + "To continue an interrupted copy, pass --resume.");
        }

        var results = new List<FlowStorageTableResult>(tables.Count);
        foreach (var table in tables)
        {
            var rows = await source.CountAsync(table, cutoff, cancellationToken).ConfigureAwait(false);
            results.Add(new FlowStorageTableResult
            {
                Table = table.SqlName,
                SourceName = table.NameOn(_options.SourceProvider),
                TargetName = table.NameOn(_options.TargetProvider),
                SourceRows = rows,
            });
            Report(progress, FlowStorageCopyPhase.Preflight, table.SqlName, 0, rows, $"{table.SqlName}: {rows} rows to copy.");
        }

        if (_options.DryRun)
        {
            if (schemaCreated)
                notes.Add("The target has no (or an incomplete) FlowOrchestrator schema; a real run creates it with the target's 2.0 migrator.");
            notes.Add("Dry run: nothing was written.");
            Report(progress, FlowStorageCopyPhase.Completed, null, 0, null, "Dry run complete.");
            return new FlowStorageCopyResult
            {
                Tables = results, DryRun = true, TargetSchemaCreated = schemaCreated, Notes = notes, Elapsed = clock.Elapsed,
            };
        }

        Report(progress, FlowStorageCopyPhase.Schema, null, 0, null, "Applying the target schema with the 2.0 migrator.");
        await target.RunMigratorAsync(_loggerFactory, cancellationToken).ConfigureAwait(false);
        if (tables.Contains(StorageCatalog.LegacyFlowEvents))
        {
            await target.ExecuteAsync(
                _options.TargetProvider == FlowStorageProvider.SqlServer ? StorageCatalog.SqlLegacyEventsDdl : StorageCatalog.PgLegacyEventsDdl,
                cancellationToken).ConfigureAwait(false);
        }

        targetSchema = await target.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
        EnsureTargetSchema(targetSchema, tables, target);

        for (var i = 0; i < tables.Count; i++)
            await CopyTableAsync(source, target, tables[i], results[i], cutoff, progress, cancellationToken).ConfigureAwait(false);

        if (target is PostgreSqlEndpoint pgTarget)
            await pgTarget.AnalyzeAsync(tables, cancellationToken).ConfigureAwait(false);
        if (target is SqlServerEndpoint sqlTarget && await sqlTarget.ResetRunSearchTokenIndexAsync(targetSchema, cancellationToken).ConfigureAwait(false))
            notes.Add("The target's run-search token index was reset; a host with UseRunSearchIndex() back-fills the copied runs on its next start.");

        if (!_options.SkipVerify)
            await VerifyTablesAsync(source, target, tables, results, cutoff, progress, cancellationToken).ConfigureAwait(false);
        else
            notes.Add("Verification was skipped (--skip-verify). Run with --verify-only before switching hosts.");

        var result = new FlowStorageCopyResult
        {
            Tables = results, TargetSchemaCreated = schemaCreated, Notes = notes, Elapsed = clock.Elapsed,
        };
        LogCopyCompleted(result.TotalCopiedRows, results.Count, clock.Elapsed);
        Report(progress, FlowStorageCopyPhase.Completed, null, result.TotalCopiedRows, null, "Copy complete.");
        ThrowIfMismatched(result);
        return result;
    }

    /// <summary>
    /// Compares source and target table by table (row count + content checksum) without writing anything.
    /// Run it after a copy, and again right before switching hosts if any time has passed.
    /// </summary>
    /// <param name="progress">Receives a report per table; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the comparison.</param>
    /// <returns>Per-table counts and checksums; <see cref="FlowStorageCopyResult.IsVerified"/> is <see langword="true"/>.</returns>
    /// <exception cref="FlowStoragePreflightException">A schema is missing or does not match this tool.</exception>
    /// <exception cref="FlowStorageVerificationException">At least one table differs.</exception>
    public async Task<FlowStorageCopyResult> VerifyAsync(
        IProgress<FlowStorageCopyProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        _options.Validate();
        var clock = Stopwatch.StartNew();
        var cutoff = NonceCutoff();
        var notes = new List<string>();

        await using var source = CreateEndpoint(_options.SourceProvider, _options.SourceConnectionString);
        await using var target = CreateEndpoint(_options.TargetProvider, _options.TargetConnectionString);
        EnsureDistinct(source, target);

        var sourceSchema = await source.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
        RequireCompleteSourceSchema(sourceSchema);
        var tables = SelectTables(sourceSchema, new List<string>());
        var targetSchema = await target.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
        RejectUnknownColumns(targetSchema, _options.TargetProvider, "target");
        EnsureTargetSchema(targetSchema, tables, target);

        var results = new List<FlowStorageTableResult>(tables.Count);
        foreach (var table in tables)
        {
            results.Add(new FlowStorageTableResult
            {
                Table = table.SqlName,
                SourceName = table.NameOn(_options.SourceProvider),
                TargetName = table.NameOn(_options.TargetProvider),
            });
        }

        await VerifyTablesAsync(source, target, tables, results, cutoff, progress, cancellationToken).ConfigureAwait(false);
        var result = new FlowStorageCopyResult { Tables = results, VerifyOnly = true, Notes = notes, Elapsed = clock.Elapsed };
        Report(progress, FlowStorageCopyPhase.Completed, null, 0, null, "Verification complete.");
        ThrowIfMismatched(result);
        return result;
    }

    /// <summary>Creates the endpoint for a provider; replaceable in tests.</summary>
    private StorageEndpoint CreateEndpoint(FlowStorageProvider provider, string connectionString) => provider switch
    {
        FlowStorageProvider.SqlServer => new SqlServerEndpoint(connectionString, _options.CommandTimeoutSeconds),
        FlowStorageProvider.PostgreSql => new PostgreSqlEndpoint(connectionString, _options.CommandTimeoutSeconds),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    private DateTimeOffset NonceCutoff()
    {
        var at = _options.NonceCutoff ?? DateTimeOffset.UtcNow;
        var utc = at.UtcTicks;
        // A whole second is also a whole microsecond, so "expires >= cutoff" selects the same rows on both
        // sides even after SQL Server -> PostgreSQL truncation.
        return new DateTimeOffset(utc - (utc % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    private static void EnsureDistinct(StorageEndpoint source, StorageEndpoint target)
    {
        if (string.Equals(source.Identity, target.Identity, StringComparison.Ordinal))
        {
            throw new FlowStoragePreflightException(
                FlowStoragePreflightFailure.SameDatabase,
                $"Source and target are the same database ({source.Display}).");
        }
    }

    private async Task<SchemaSnapshot> EnsureSourceSchemaAsync(
        StorageEndpoint source, SchemaSnapshot schema, IProgress<FlowStorageCopyProgress>? progress,
        List<string> notes, CancellationToken cancellationToken)
    {
        var provider = _options.SourceProvider;
        if (!schema.HasTable(StorageCatalog.FlowRuns.NameOn(provider)) && !schema.HasTable(StorageCatalog.FlowDefinitions.NameOn(provider)))
        {
            throw new FlowStoragePreflightException(
                FlowStoragePreflightFailure.SourceSchemaMissing,
                $"The source ({source.Display}) has no FlowOrchestrator tables in the connection's default schema. Check the connection string and the database.");
        }

        var missing = SchemaChecks.FindMissing(schema, provider, StorageCatalog.CoreTables);
        if (missing.Count > 0)
        {
            if (!_options.MigrateSourceSchema || _options.DryRun)
            {
                throw new FlowStoragePreflightException(
                    FlowStoragePreflightFailure.SourceSchemaOutdated,
                    $"The source schema predates FlowOrchestrator 2.0 (missing: {string.Join(", ", missing)}). "
                    + "Start the source host on FlowOrchestrator 2.0 once (its migrator upgrades the schema in place), "
                    + "or pass --migrate-source to let this tool run the 2.0 migrator on the source (additive only: new tables, nullable columns, indexes)."
                    + (_options.DryRun ? " (A dry run never migrates the source.)" : string.Empty));
            }

            Report(progress, FlowStorageCopyPhase.Schema, null, 0, null, "Upgrading the source schema with the 2.0 migrator (--migrate-source).");
            await source.RunMigratorAsync(_loggerFactory, cancellationToken).ConfigureAwait(false);
            schema = await source.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
            missing = SchemaChecks.FindMissing(schema, provider, StorageCatalog.CoreTables);
            if (missing.Count > 0)
            {
                throw new FlowStoragePreflightException(
                    FlowStoragePreflightFailure.SourceSchemaOutdated,
                    $"The source schema is still missing {string.Join(", ", missing)} after the 2.0 migrator ran.");
            }

            notes.Add("The source schema was upgraded in place by the 2.0 migrator (--migrate-source).");
        }

        RejectUnknownColumns(schema, provider, "source");
        return schema;
    }

    private void RequireCompleteSourceSchema(SchemaSnapshot schema)
    {
        var missing = SchemaChecks.FindMissing(schema, _options.SourceProvider, StorageCatalog.CoreTables);
        if (missing.Count > 0)
        {
            throw new FlowStoragePreflightException(
                FlowStoragePreflightFailure.SourceSchemaOutdated,
                $"The source schema is missing {string.Join(", ", missing)}; there is nothing comparable to verify.");
        }

        RejectUnknownColumns(schema, _options.SourceProvider, "source");
    }

    private static void RejectUnknownColumns(SchemaSnapshot schema, FlowStorageProvider provider, string side)
    {
        var unknown = SchemaChecks.FindUnknownColumns(schema, provider, StorageCatalog.CoreTables);
        if (unknown.Count > 0)
        {
            throw new FlowStoragePreflightException(
                FlowStoragePreflightFailure.SchemaNewerThanTool,
                $"The {side} schema has columns this version of the tool does not know: {string.Join(", ", unknown)}. "
                + "Copying would drop them. Use the FlowOrchestrator.Cli version that matches the FlowOrchestrator version that created the schema.");
        }
    }

    private static bool HasCompleteSchema(SchemaSnapshot schema, FlowStorageProvider provider) =>
        SchemaChecks.FindMissing(schema, provider, StorageCatalog.CoreTables).Count == 0;

    private void EnsureTargetSchema(SchemaSnapshot schema, IReadOnlyList<StorageTable> tables, StorageEndpoint target)
    {
        var missing = SchemaChecks.FindMissing(schema, _options.TargetProvider, tables);
        if (missing.Count > 0)
        {
            throw new FlowStoragePreflightException(
                FlowStoragePreflightFailure.TargetSchemaIncomplete,
                $"The target ({target.Display}) is missing {string.Join(", ", missing)}.");
        }
    }

    /// <summary>The core tables, plus the legacy event log when the source has it and it is not skipped.</summary>
    private List<StorageTable> SelectTables(SchemaSnapshot sourceSchema, List<string> notes)
    {
        var tables = new List<StorageTable>(StorageCatalog.CoreTables);
        var legacy = StorageCatalog.LegacyFlowEvents;
        if (!sourceSchema.HasTable(legacy.NameOn(_options.SourceProvider)))
            return tables;

        if (_options.SkipLegacyEvents)
        {
            notes.Add($"The legacy 1.x event table {legacy.NameOn(_options.SourceProvider)} was not copied (--skip-legacy-events).");
            return tables;
        }

        if (SchemaChecks.FindMissing(sourceSchema, _options.SourceProvider, [legacy]).Count > 0)
        {
            notes.Add($"The legacy table {legacy.NameOn(_options.SourceProvider)} does not have the 1.34 shape and was not copied.");
            return tables;
        }

        tables.Add(legacy);
        notes.Add($"The legacy 1.x event table {legacy.NameOn(_options.SourceProvider)} was copied to {legacy.NameOn(_options.TargetProvider)} (2.0 does not read it; it is kept for a 1.34 rollback).");
        return tables;
    }

    private void AddSourceNotes(SchemaSnapshot sourceSchema, List<string> notes)
    {
        if (_options.SourceProvider == FlowStorageProvider.SqlServer && sourceSchema.HasTable("FlowRunSearchTokens"))
        {
            notes.Add(_options.TargetProvider == FlowStorageProvider.SqlServer
                ? "The run-search token index (FlowRunSearchTokens) was not copied: it is derived data. Keep UseRunSearchIndex() on the target host and it back-fills."
                : "The run-search token index (FlowRunSearchTokens) was not copied: PostgreSQL answers deep search with its pg_trgm indexes instead.");
        }

        if (_options.SourceProvider == FlowStorageProvider.SqlServer && _options.TargetProvider == FlowStorageProvider.PostgreSql)
            notes.Add("Timestamps were truncated from 100 ns (SQL Server) to 1 microsecond (PostgreSQL) and stored in UTC.");
        notes.Add("Hangfire storage is not copied: drain its queues before the copy. Recurring cron jobs are re-registered when the host starts on the new storage (overrides and pause state come from the copied schedule state).");
    }

    private async Task CheckActiveRunsAsync(StorageEndpoint source, List<string> notes, CancellationToken cancellationToken)
    {
        var (count, sample) = await source.ActiveRunsAsync(cancellationToken).ConfigureAwait(false);
        if (count == 0)
            return;

        var ids = string.Join(", ", sample);
        if (_options.Force)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{count} run(s) in status Running were copied as they are (--force), e.g. {ids}. The target host re-dispatches their ready steps on start-up; the source host must stay stopped."));
            return;
        }

        throw new FlowStoragePreflightException(
            FlowStoragePreflightFailure.ActiveRuns,
            string.Create(CultureInfo.InvariantCulture, $"The source has {count} run(s) in status Running (e.g. {ids}). ")
                + "In-flight runs cannot be moved safely: stop new triggers (disable the flows or stop the webhook / cron sources), let the running runs finish or cancel them "
                + "from the dashboard, then run the copy again. --force copies them anyway, as they are.");
    }

    private async Task<List<string>> FindNonEmptyTablesAsync(
        StorageEndpoint target, SchemaSnapshot targetSchema, IReadOnlyList<StorageTable> tables, CancellationToken cancellationToken)
    {
        var nonEmpty = new List<string>();
        foreach (var name in tables.Select(table => table.NameOn(_options.TargetProvider)).Where(targetSchema.HasTable))
        {
            var hasRows = await target.HasRowsAsync(name, cancellationToken).ConfigureAwait(false);
            if (hasRows)
                nonEmpty.Add(name);
        }

        return nonEmpty;
    }

    private async Task CopyTableAsync(
        StorageEndpoint source, StorageEndpoint target, StorageTable table, FlowStorageTableResult result,
        DateTimeOffset cutoff, IProgress<FlowStorageCopyProgress>? progress, CancellationToken cancellationToken)
    {
        if (_options.Resume)
        {
            var existing = await target.CountAsync(table, cutoff, cancellationToken).ConfigureAwait(false);
            if (existing > 0)
            {
                // A table is committed whole or not at all, so a non-empty table on resume is a finished one —
                // unless its count disagrees. Expiring nonces cannot be compared by count across runs.
                if (existing == result.SourceRows || table.ExpiryOrdinal >= 0)
                {
                    result.SkippedAsComplete = true;
                    Report(progress, FlowStorageCopyPhase.Copy, table.SqlName, existing, result.SourceRows, $"{table.SqlName}: already copied, skipped (--resume).");
                    return;
                }

                throw new FlowStoragePreflightException(
                    FlowStoragePreflightFailure.ResumeMismatch,
                    string.Create(CultureInfo.InvariantCulture, $"{table.NameOn(_options.TargetProvider)} on the target holds {existing} rows, the source {result.SourceRows}. ")
                        + "The source changed since the interrupted copy, or the target was written to. Start over on an empty target.");
            }
        }

        Report(progress, FlowStorageCopyPhase.Copy, table.SqlName, 0, result.SourceRows, $"{table.SqlName}: copying.");
        LogTableStarted(table.SqlName, result.SourceRows);
        var copied = 0L;
        try
        {
            await using var writer = await target.BeginTableAsync(table, cancellationToken).ConfigureAwait(false);
            object?[]? cursor = null;
            while (true)
            {
                var batch = await source.ReadBatchAsync(table, cutoff, cursor, _options.BatchSize, cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0)
                    break;

                // The cursor keeps the key as read; keys are never rewritten by the preparation below anyway.
                cursor = batch[^1];
                var prepared = new List<object?[]>(batch.Count);
                foreach (var row in batch)
                {
                    var copy = new object?[row.Length];
                    for (var c = 0; c < row.Length; c++)
                        copy[c] = ValueNormalizer.PrepareForTarget(_options.TargetProvider, table, table.Columns[c], row[c], row);
                    prepared.Add(copy);
                }

                await writer.WriteAsync(prepared, cancellationToken).ConfigureAwait(false);
                copied += prepared.Count;
                Report(progress, FlowStorageCopyPhase.Copy, table.SqlName, copied, result.SourceRows,
                    string.Create(CultureInfo.InvariantCulture, $"{table.SqlName}: {copied}/{result.SourceRows} rows."));
            }

            await writer.CommitAsync(reseedIdentity: copied > 0, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            throw new FlowStorageCopyException(
                table.SqlName,
                $"{table.SqlName}: two source rows collide on the SQL Server primary key ({ex.Message}). SQL Server compares keys with the "
                + "database collation, usually case- and trailing-space-insensitive, while PostgreSQL compares them exactly; rename one of the "
                + "colliding keys on the source, or create the target database with a case-sensitive collation (e.g. Latin1_General_100_CS_AS_SC_UTF8). "
                + "This table was rolled back; earlier tables are committed - empty the target and copy again.",
                ex);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new FlowStorageCopyException(
                table.SqlName,
                $"{table.SqlName}: two source rows collide on the PostgreSQL primary key ({ex.MessageText}). This table was rolled back; earlier tables are committed.",
                ex);
        }

        result.CopiedRows = copied;
        LogTableCompleted(table.SqlName, copied);
    }

    private async Task VerifyTablesAsync(
        StorageEndpoint source, StorageEndpoint target, IReadOnlyList<StorageTable> tables, List<FlowStorageTableResult> results,
        DateTimeOffset cutoff, IProgress<FlowStorageCopyProgress>? progress, CancellationToken cancellationToken)
    {
        for (var i = 0; i < tables.Count; i++)
        {
            var table = tables[i];
            Report(progress, FlowStorageCopyPhase.Verify, table.SqlName, 0, null, $"{table.SqlName}: verifying.");
            var sourceSum = ChecksumAsync(source, table, cutoff, cancellationToken);
            var targetSum = ChecksumAsync(target, table, cutoff, cancellationToken);
            await Task.WhenAll(sourceSum, targetSum).ConfigureAwait(false);

            var result = results[i];
            var (sourceCount, sourceHash) = sourceSum.Result;
            var (targetCount, targetHash) = targetSum.Result;
            result.SourceRows = sourceCount;
            result.TargetRows = targetCount;
            result.SourceChecksum = sourceHash;
            result.TargetChecksum = targetHash;
            result.Verified = sourceCount == targetCount && string.Equals(sourceHash, targetHash, StringComparison.Ordinal);
            Report(progress, FlowStorageCopyPhase.Verify, table.SqlName, targetCount, sourceCount,
                result.Verified == true
                    ? string.Create(CultureInfo.InvariantCulture, $"{table.SqlName}: {targetCount} rows, checksum {targetHash} - match.")
                    : string.Create(CultureInfo.InvariantCulture, $"{table.SqlName}: MISMATCH - source {sourceCount} rows / {sourceHash}, target {targetCount} rows / {targetHash}."));
        }
    }

    private async Task<(long Count, string Checksum)> ChecksumAsync(
        StorageEndpoint endpoint, StorageTable table, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var checksum = new TableChecksum();
        object?[]? cursor = null;
        while (true)
        {
            var batch = await endpoint.ReadBatchAsync(table, cutoff, cursor, _options.BatchSize, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
                break;
            foreach (var row in batch)
                checksum.Add(table, row);
            cursor = batch[^1];
        }

        return (checksum.Count, checksum.Value);
    }

    private static void ThrowIfMismatched(FlowStorageCopyResult result)
    {
        var mismatched = result.Tables
            .Where(table => table.Verified == false)
            .Select(table => string.Create(CultureInfo.InvariantCulture,
                $"{table.Table} (source {table.SourceRows} rows / {table.SourceChecksum}, target {table.TargetRows} rows / {table.TargetChecksum})"))
            .ToList();

        if (mismatched.Count > 0)
        {
            throw new FlowStorageVerificationException(
                "The target does not match the source: " + string.Join("; ", mismatched)
                + ". If a host is still writing to the source, stop it and copy again into an empty target.",
                result);
        }
    }

    private static void Report(
        IProgress<FlowStorageCopyProgress>? progress, FlowStorageCopyPhase phase, string? table, long done, long? total, string message) =>
        progress?.Report(new FlowStorageCopyProgress(phase, table, done, total, message));

    [LoggerMessage(EventId = 7401, Level = LogLevel.Information, Message = "Copying {Table} ({Rows} source rows).")]
    private partial void LogTableStarted(string table, long rows);

    [LoggerMessage(EventId = 7402, Level = LogLevel.Information, Message = "Copied {Table}: {Rows} rows.")]
    private partial void LogTableCompleted(string table, long rows);

    [LoggerMessage(EventId = 7403, Level = LogLevel.Information, Message = "Storage copy completed: {Rows} rows in {Tables} tables in {Elapsed}.")]
    private partial void LogCopyCompleted(long rows, int tables, TimeSpan elapsed);
}
