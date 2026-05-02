namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// In-memory bucket aggregator used by SQL store implementations to compute
/// <see cref="RunTimeseriesBucket"/> series after streaming raw run rows back from the database.
/// Doing percentiles in client code is significantly cheaper than asking SQL Server / PostgreSQL
/// to evaluate <c>PERCENTILE_CONT</c> across a few thousand rows.
/// </summary>
public static class TimeseriesAggregator
{
    /// <summary>
    /// Aggregates a flat list of (StartedAt, Status, DurationMs) rows into time-bucketed
    /// counts and percentiles aligned to the supplied window. Empty buckets are emitted
    /// so the caller can render a contiguous timeline.
    /// </summary>
    public static IReadOnlyList<RunTimeseriesBucket> Aggregate(
        IEnumerable<(DateTimeOffset StartedAt, string Status, double? DurationMs)> rows,
        RunTimeseriesGranularity granularity,
        DateTimeOffset since,
        DateTimeOffset until)
    {
        var bucketSize = granularity == RunTimeseriesGranularity.Hour ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        var anchor = granularity == RunTimeseriesGranularity.Hour
            ? new DateTimeOffset(since.UtcDateTime.Year, since.UtcDateTime.Month, since.UtcDateTime.Day, since.UtcDateTime.Hour, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(since.UtcDateTime.Date, TimeSpan.Zero);
        if (anchor > since) anchor -= bucketSize;

        var totalBuckets = (int)Math.Ceiling((until - anchor).TotalMilliseconds / bucketSize.TotalMilliseconds);
        if (totalBuckets <= 0) return Array.Empty<RunTimeseriesBucket>();
        if (totalBuckets > 10_000) totalBuckets = 10_000;

        var buckets = new RunTimeseriesBucket[totalBuckets];
        var durations = new List<double>[totalBuckets];
        for (int i = 0; i < totalBuckets; i++)
        {
            buckets[i] = new RunTimeseriesBucket { Timestamp = anchor + bucketSize * i };
            durations[i] = new List<double>(capacity: 4);
        }

        foreach (var (startedAt, status, durMs) in rows)
        {
            if (startedAt < anchor || startedAt >= until) continue;
            var idx = (int)((startedAt - anchor).TotalMilliseconds / bucketSize.TotalMilliseconds);
            if (idx < 0 || idx >= totalBuckets) continue;

            var b = buckets[idx];
            b.Total++;
            switch (status)
            {
                case "Succeeded": b.Succeeded++; break;
                case "Failed": b.Failed++; break;
                case "Cancelled": b.Cancelled++; break;
                default: b.Running++; break;
            }
            if (durMs.HasValue && status != "Running")
            {
                durations[idx].Add(durMs.Value);
            }
        }

        for (int i = 0; i < totalBuckets; i++)
        {
            var d = durations[i];
            if (d.Count == 0) continue;
            d.Sort();
            buckets[i].P50DurationMs = Percentile(d, 0.50);
            buckets[i].P95DurationMs = Percentile(d, 0.95);
        }

        return buckets;
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 1) return sorted[0];
        var rank = (sorted.Count - 1) * p;
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}
