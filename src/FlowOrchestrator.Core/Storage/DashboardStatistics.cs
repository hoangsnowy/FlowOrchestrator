namespace FlowOrchestrator.Core.Storage;

public sealed class DashboardStatistics
{
    public int TotalFlows { get; set; }
    public int ActiveRuns { get; set; }
    public int CompletedToday { get; set; }
    public int FailedToday { get; set; }
}
