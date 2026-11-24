namespace FlowOrchestrator.StorageMigration;

/// <summary>The stage a <see cref="FlowStorageCopier"/> run is in.</summary>
public enum FlowStorageCopyPhase
{
    /// <summary>Checking schemas, in-flight runs and the target's emptiness.</summary>
    Preflight,

    /// <summary>Running the target (or, when requested, the source) migrator.</summary>
    Schema,

    /// <summary>Bulk-copying one table.</summary>
    Copy,

    /// <summary>Comparing row counts and checksums of one table on both sides.</summary>
    Verify,

    /// <summary>The run has finished.</summary>
    Completed,

    /// <summary>
    /// Something the operator should fix, reported before any data is read; the run continues. Today: a SQL Server
    /// connection string that disables encryption or certificate validation (see <see cref="FlowStorageCopyResult.Warnings"/>).
    /// </summary>
    Warning,
}

/// <summary>A progress report from <see cref="FlowStorageCopier"/>.</summary>
/// <param name="Phase">The current stage.</param>
/// <param name="Table">The logical table being processed (the SQL Server name, e.g. <c>FlowRuns</c>), or <see langword="null"/>.</param>
/// <param name="RowsDone">Rows copied or verified so far in <paramref name="Table"/>.</param>
/// <param name="RowsTotal">Rows the source holds for <paramref name="Table"/>, when known.</param>
/// <param name="Message">A human-readable description of the step.</param>
public readonly record struct FlowStorageCopyProgress(
    FlowStorageCopyPhase Phase,
    string? Table,
    long RowsDone,
    long? RowsTotal,
    string Message);
