namespace FlowOrchestrator.StorageMigration;

/// <summary>What happened to one table during a <see cref="FlowStorageCopier"/> run.</summary>
public sealed class FlowStorageTableResult
{
    /// <summary>Logical table name (the SQL Server spelling, e.g. <c>FlowSteps</c>).</summary>
    public required string Table { get; init; }

    /// <summary>Physical name on the source.</summary>
    public required string SourceName { get; init; }

    /// <summary>Physical name on the target.</summary>
    public required string TargetName { get; init; }

    /// <summary>Rows the source holds (after the table's filter, e.g. unexpired nonces only).</summary>
    public long SourceRows { get; set; }

    /// <summary>Rows written to the target by this run (0 for a dry run, a verify-only run, or a resumed table).</summary>
    public long CopiedRows { get; set; }

    /// <summary>The table was already complete on the target and was skipped (<see cref="FlowStorageCopyOptions.Resume"/>).</summary>
    public bool SkippedAsComplete { get; set; }

    /// <summary>Rows the target holds after the run, when verified.</summary>
    public long? TargetRows { get; set; }

    /// <summary>Order-independent content checksum of the source rows, when verified.</summary>
    public string? SourceChecksum { get; set; }

    /// <summary>Order-independent content checksum of the target rows, when verified.</summary>
    public string? TargetChecksum { get; set; }

    /// <summary>
    /// <see langword="true"/> when counts and checksums match; <see langword="false"/> when they differ;
    /// <see langword="null"/> when the table was not verified.
    /// </summary>
    public bool? Verified { get; set; }
}

/// <summary>The outcome of a <see cref="FlowStorageCopier"/> run.</summary>
public sealed class FlowStorageCopyResult
{
    /// <summary>Per-table results, in copy order.</summary>
    public required IReadOnlyList<FlowStorageTableResult> Tables { get; init; }

    /// <summary>The run wrote nothing (<see cref="FlowStorageCopyOptions.DryRun"/>).</summary>
    public bool DryRun { get; init; }

    /// <summary>The run only compared the two sides (<see cref="FlowStorageCopier.VerifyAsync"/>).</summary>
    public bool VerifyOnly { get; init; }

    /// <summary>The target schema did not exist and was (or, in a dry run, would be) created by the target migrator.</summary>
    public bool TargetSchemaCreated { get; init; }

    /// <summary>Things the operator should know: skipped tables, precision notes, follow-up actions.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>
    /// Transport-security weaknesses in the connection strings the operator supplied: a SQL Server connection
    /// with <c>Encrypt=False</c>/<c>Optional</c>, or with <c>TrustServerCertificate=True</c>.
    /// </summary>
    /// <remarks>
    /// The copy honours the connection strings as given and never overrides them. Each warning is also reported as a
    /// <see cref="FlowStorageCopyPhase.Warning"/> progress event and logged, before any data is read.
    /// </remarks>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Wall-clock duration of the run.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Sum of <see cref="FlowStorageTableResult.CopiedRows"/> over every table.</summary>
    public long TotalCopiedRows
    {
        get
        {
            long total = 0;
            foreach (var table in Tables)
                total += table.CopiedRows;
            return total;
        }
    }

    /// <summary><see langword="true"/> when every verified table matched (and at least one was verified).</summary>
    public bool IsVerified
    {
        get
        {
            var any = false;
            foreach (var table in Tables)
            {
                if (table.Verified == false)
                    return false;
                any |= table.Verified == true;
            }

            return any;
        }
    }
}
