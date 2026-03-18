namespace FlowOrchestrator.Dashboard;

public sealed class FlowDashboardOptions
{
    public const string DefaultSectionName = "FlowDashboard";

    public FlowDashboardBasicAuthOptions BasicAuth { get; set; } = new();
}

public sealed class FlowDashboardBasicAuthOptions
{
    public string? Username { get; set; }

    public string? Password { get; set; }

    public string Realm { get; set; } = "FlowOrchestrator Dashboard";

    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrWhiteSpace(Password);
}
