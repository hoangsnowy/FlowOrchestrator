using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.StorageMigration;

/// <summary>Configures one <see cref="FlowStorageCopier"/> run: where to read, where to write, and how.</summary>
public sealed class FlowStorageCopyOptions
{
    /// <summary>Default for <see cref="BatchSize"/>.</summary>
    public const int DefaultBatchSize = 1_000;

    /// <summary>Largest accepted <see cref="BatchSize"/>.</summary>
    public const int MaxBatchSize = 100_000;

    /// <summary>The backend the data is read from. It is never written to (unless <see cref="MigrateSourceSchema"/> is set).</summary>
    public FlowStorageProvider SourceProvider { get; set; }

    /// <summary>Connection string of the source FlowOrchestrator database.</summary>
    public string SourceConnectionString { get; set; } = string.Empty;

    /// <summary>The backend the data is written to.</summary>
    public FlowStorageProvider TargetProvider { get; set; }

    /// <summary>
    /// Connection string of the target database. The database must exist; the FlowOrchestrator tables are
    /// created by the 2.0 migrator of <see cref="TargetProvider"/> when missing.
    /// </summary>
    public string TargetConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Rows read from the source per round trip and written to the target per bulk-load call.
    /// A batch is also cut short once it holds about 64 MB of text, so wide <c>FlowSteps</c> rows do not
    /// multiply memory by this number.
    /// </summary>
    public int BatchSize { get; set; } = DefaultBatchSize;

    /// <summary>
    /// Runs every preflight check and counts the rows that would be copied, without writing anything to either
    /// database — not even the target schema.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>Skips the post-copy verification (row counts + content checksum). Not recommended.</summary>
    public bool SkipVerify { get; set; }

    /// <summary>
    /// Copies even when the source still has runs in status <c>Running</c>. Those runs are copied as they are;
    /// the target host's recovery service re-dispatches their ready steps on start-up, so the source host must
    /// be stopped for good, or the same step may execute on both.
    /// </summary>
    public bool Force { get; set; }

    /// <summary>
    /// Continues an interrupted copy: tables whose target row count already equals the source row count are
    /// skipped, empty tables are copied. A table that is neither still fails. Each table is copied in its own
    /// transaction, so an interrupted copy never leaves a half-filled table behind.
    /// </summary>
    public bool Resume { get; set; }

    /// <summary>
    /// Upgrades an older source schema in place with the source provider's 2.0 migrator (additive only:
    /// new tables, nullable columns and indexes) instead of refusing. Off by default so the source stays
    /// untouched and remains a valid rollback point.
    /// </summary>
    public bool MigrateSourceSchema { get; set; }

    /// <summary>
    /// Does not copy the legacy 1.x run-event table (<c>FlowEvents</c> / <c>flow_events</c>) even when the
    /// source still has it. 2.0 never reads that table.
    /// </summary>
    public bool SkipLegacyEvents { get; set; }

    /// <summary>
    /// The instant that decides which webhook replay nonces are still valid (only those expiring at or after it
    /// are copied and verified). <see langword="null"/> uses the current time, truncated to the whole second.
    /// </summary>
    public DateTimeOffset? NonceCutoff { get; set; }

    /// <summary>Logger factory for the copier and the storage migrators it runs. <see langword="null"/> logs nothing.</summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>Command timeout, in seconds, for counting, reading and bulk-load commands. 0 waits indefinitely.</summary>
    public int CommandTimeoutSeconds { get; set; } = 600;

    /// <summary>Validates the option combination.</summary>
    /// <exception cref="ArgumentException">Thrown when a connection string is missing or a value is out of range.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceConnectionString))
            throw new ArgumentException("A source connection string is required.", nameof(SourceConnectionString));
        if (string.IsNullOrWhiteSpace(TargetConnectionString))
            throw new ArgumentException("A target connection string is required.", nameof(TargetConnectionString));
        if (BatchSize is < 1 or > MaxBatchSize)
            throw new ArgumentException($"Batch size must be between 1 and {MaxBatchSize}.", nameof(BatchSize));
        if (CommandTimeoutSeconds < 0)
            throw new ArgumentException("Command timeout cannot be negative.", nameof(CommandTimeoutSeconds));
        if (!Enum.IsDefined(SourceProvider))
            throw new ArgumentException("Unknown source provider.", nameof(SourceProvider));
        if (!Enum.IsDefined(TargetProvider))
            throw new ArgumentException("Unknown target provider.", nameof(TargetProvider));
    }
}
