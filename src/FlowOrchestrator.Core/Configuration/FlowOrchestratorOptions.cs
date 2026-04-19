namespace FlowOrchestrator.Core.Configuration;

/// <summary>
/// Configuration options for the FlowOrchestrator runtime. Passed to
/// <c>AddFlowOrchestrator(options => ...)</c> during DI setup.
/// </summary>
public sealed class FlowOrchestratorOptions
{
    /// <summary>
    /// Primary database connection string used by the configured storage backend.
    /// Set automatically when calling <c>UseSqlServer()</c> or <c>UsePostgreSql()</c>.
    /// </summary>
    public string? ConnectionString { get; set; }
}
