namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// One bucket in a run timeseries query. Counts are by terminal status; <see cref="Running"/>
/// captures runs whose start falls in the bucket but which have not yet completed. Duration
/// percentiles are computed across runs that have completed (<see cref="FlowRunRecord.CompletedAt"/>
/// is non-null) and started inside the bucket.
/// </summary>
public sealed class RunTimeseriesBucket
{
    /// <summary>UTC timestamp at the start of the bucket interval (inclusive).</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Total number of runs whose start time falls in this bucket.</summary>
    public int Total { get; set; }

    /// <summary>Runs that completed with status <c>Succeeded</c>.</summary>
    public int Succeeded { get; set; }

    /// <summary>Runs that completed with status <c>Failed</c>.</summary>
    public int Failed { get; set; }

    /// <summary>Runs that completed with status <c>Cancelled</c>.</summary>
    public int Cancelled { get; set; }

    /// <summary>Runs in this bucket that have not yet reached a terminal state.</summary>
    public int Running { get; set; }

    /// <summary>Median run duration in milliseconds across completed runs in this bucket; <see langword="null"/> when the bucket has no completed runs.</summary>
    public double? P50DurationMs { get; set; }

    /// <summary>95th-percentile run duration in milliseconds across completed runs in this bucket; <see langword="null"/> when the bucket has no completed runs.</summary>
    public double? P95DurationMs { get; set; }
}

/// <summary>
/// Bucket size for a run timeseries query.
/// </summary>
public enum RunTimeseriesGranularity
{
    /// <summary>One bucket per UTC hour.</summary>
    Hour,

    /// <summary>One bucket per UTC day.</summary>
    Day
}
