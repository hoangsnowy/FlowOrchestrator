namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Aggregate counts displayed on the dashboard overview panel.
/// Returned by <see cref="IFlowRunStore.GetStatisticsAsync"/>.
/// </summary>
public sealed class DashboardStatistics
{
    /// <summary>Total number of registered (enabled) flow definitions.</summary>
    public int TotalFlows { get; set; }

    /// <summary>Number of runs currently in <c>Running</c> status.</summary>
    public int ActiveRuns { get; set; }

    /// <summary>Number of runs that completed with <c>Succeeded</c> status today (UTC).</summary>
    public int CompletedToday { get; set; }

    /// <summary>Number of runs that completed with <c>Failed</c> status today (UTC).</summary>
    public int FailedToday { get; set; }
}
