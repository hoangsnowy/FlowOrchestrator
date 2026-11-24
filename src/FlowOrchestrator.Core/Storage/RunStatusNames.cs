namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// The run statuses the engine persists on <see cref="FlowRunRecord.Status"/>, and a helper that maps a
/// caller-supplied status filter onto that exact spelling.
/// </summary>
/// <remarks>
/// Run-list filters arrive from the dashboard query string or user code in any casing. The in-memory
/// store compares with <see cref="StringComparison.OrdinalIgnoreCase"/> and SQL Server's default collation
/// is case-insensitive, but PostgreSQL compares <c>status</c> byte for byte. Canonicalising the parameter
/// (rather than wrapping the column in <c>lower()</c>) keeps the filter an equality on the bare column,
/// so <c>ix_flow_runs_status</c> and <c>ix_flow_runs_flow_id_status_started_at</c> stay usable.
/// </remarks>
public static class RunStatusNames
{
    /// <summary>A run with at least one step not yet terminal.</summary>
    public const string Running = "Running";

    /// <summary>A run whose terminal classification is success.</summary>
    public const string Succeeded = "Succeeded";

    /// <summary>A run with an unhandled failed step.</summary>
    public const string Failed = "Failed";

    /// <summary>A run whose every leaf step was skipped.</summary>
    public const string Skipped = "Skipped";

    /// <summary>A run a user cancelled.</summary>
    public const string Cancelled = "Cancelled";

    /// <summary>A run that exceeded its timeout deadline.</summary>
    public const string TimedOut = "TimedOut";

    /// <summary>
    /// Returns the persisted spelling of <paramref name="status"/> when it names a known run status in any
    /// casing; otherwise returns <paramref name="status"/> unchanged (a custom store may persist its own).
    /// </summary>
    /// <param name="status">The requested status filter.</param>
    /// <returns>The canonical status string, or <paramref name="status"/> itself.</returns>
    /// <remarks>Allocation-free: returns one of the constants or the input instance.</remarks>
    public static string Canonicalize(string status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var span = status.AsSpan().Trim();
        if (span.Equals(Running, StringComparison.OrdinalIgnoreCase)) return Running;
        if (span.Equals(Succeeded, StringComparison.OrdinalIgnoreCase)) return Succeeded;
        if (span.Equals(Failed, StringComparison.OrdinalIgnoreCase)) return Failed;
        if (span.Equals(Skipped, StringComparison.OrdinalIgnoreCase)) return Skipped;
        if (span.Equals(Cancelled, StringComparison.OrdinalIgnoreCase)) return Cancelled;
        if (span.Equals(TimedOut, StringComparison.OrdinalIgnoreCase)) return TimedOut;
        return status;
    }
}
