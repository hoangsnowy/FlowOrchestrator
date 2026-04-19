namespace FlowOrchestrator.Dashboard;

/// <summary>
/// Root configuration for the FlowOrchestrator dashboard, read from
/// <c>appsettings.json</c> under the <see cref="DefaultSectionName"/> key via
/// <c>AddFlowDashboard(configuration)</c>.
/// </summary>
public sealed class FlowDashboardOptions
{
    /// <summary>Default configuration section name (<c>"FlowDashboard"</c>).</summary>
    public const string DefaultSectionName = "FlowDashboard";

    /// <summary>HTTP Basic Auth settings. Disabled when <see cref="FlowDashboardBasicAuthOptions.Username"/> is not set.</summary>
    public FlowDashboardBasicAuthOptions BasicAuth { get; set; } = new();

    /// <summary>UI branding settings (title, subtitle, logo URL).</summary>
    public FlowDashboardBrandingOptions Branding { get; set; } = new();
}

/// <summary>
/// Optional HTTP Basic Auth protection for the dashboard and its REST API.
/// Both <see cref="Username"/> and <see cref="Password"/> must be set to activate protection.
/// </summary>
public sealed class FlowDashboardBasicAuthOptions
{
    /// <summary>Expected Basic Auth username. <see langword="null"/> disables authentication.</summary>
    public string? Username { get; set; }

    /// <summary>Expected Basic Auth password.</summary>
    public string? Password { get; set; }

    /// <summary>WWW-Authenticate realm returned in 401 responses.</summary>
    public string Realm { get; set; } = "FlowOrchestrator Dashboard";

    /// <summary>
    /// <see langword="true"/> when both <see cref="Username"/> and <see cref="Password"/> are non-empty,
    /// indicating that Basic Auth should be enforced on dashboard endpoints.
    /// </summary>
    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrWhiteSpace(Password);
}

/// <summary>
/// Branding customisation for the dashboard SPA's header area.
/// </summary>
public sealed class FlowDashboardBrandingOptions
{
    /// <summary>Page title and header text. Defaults to <c>"FlowOrchestrator Dashboard"</c>.</summary>
    public string Title { get; set; } = "FlowOrchestrator Dashboard";

    /// <summary>Secondary header line displayed below the title.</summary>
    public string Subtitle { get; set; } = "Dashboard";

    /// <summary>URL of a logo image to display in the header. <see langword="null"/> hides the logo.</summary>
    public string? LogoUrl { get; set; }
}
