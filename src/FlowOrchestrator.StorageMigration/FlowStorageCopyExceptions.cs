namespace FlowOrchestrator.StorageMigration;

/// <summary>Why a <see cref="FlowStorageCopier"/> run refused to start.</summary>
public enum FlowStoragePreflightFailure
{
    /// <summary>The source has runs in status <c>Running</c>; drain or cancel them first (or pass <see cref="FlowStorageCopyOptions.Force"/>).</summary>
    ActiveRuns,

    /// <summary>The target already holds FlowOrchestrator rows; copy into an empty database (or pass <see cref="FlowStorageCopyOptions.Resume"/>).</summary>
    TargetNotEmpty,

    /// <summary>The source has no FlowOrchestrator schema at all.</summary>
    SourceSchemaMissing,

    /// <summary>The source schema predates the 2.0 migrator; start the source host on 2.0 once, or pass <see cref="FlowStorageCopyOptions.MigrateSourceSchema"/>.</summary>
    SourceSchemaOutdated,

    /// <summary>A schema has columns this version of the tool does not know, so copying would silently drop them.</summary>
    SchemaNewerThanTool,

    /// <summary>The target schema is missing tables or columns even after the target migrator ran (or, in a dry run / verify, before).</summary>
    TargetSchemaIncomplete,

    /// <summary>Source and target are the same database.</summary>
    SameDatabase,

    /// <summary>A resumed table holds a different number of rows than the source, so it can be neither skipped nor copied.</summary>
    ResumeMismatch,
}

/// <summary>Thrown when a preflight rule refuses the copy. Nothing has been written when it is thrown.</summary>
public sealed class FlowStoragePreflightException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="failure">The rule that refused.</param>
    /// <param name="message">A message that says what to do about it.</param>
    public FlowStoragePreflightException(FlowStoragePreflightFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    /// <summary>The rule that refused.</summary>
    public FlowStoragePreflightFailure Failure { get; }
}

/// <summary>Thrown when the target does not read back equal to the source after a copy, or in <see cref="FlowStorageCopier.VerifyAsync"/>.</summary>
public sealed class FlowStorageVerificationException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Names every table that differs.</param>
    /// <param name="result">The full result, including the per-table counts and checksums.</param>
    public FlowStorageVerificationException(string message, FlowStorageCopyResult result)
        : base(message)
    {
        Result = result;
    }

    /// <summary>The full result, including the per-table counts and checksums.</summary>
    public FlowStorageCopyResult Result { get; }
}

/// <summary>
/// Thrown when a row cannot be stored on the target as it is (a value the target type cannot hold, a key that
/// collides under the target's collation). The table being copied is rolled back; earlier tables stay committed.
/// </summary>
public sealed class FlowStorageCopyException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="table">The logical table being copied.</param>
    /// <param name="message">Names the row and column and what is wrong with it.</param>
    /// <param name="innerException">The provider exception, if any.</param>
    public FlowStorageCopyException(string table, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Table = table;
    }

    /// <summary>The logical table being copied.</summary>
    public string Table { get; }
}
