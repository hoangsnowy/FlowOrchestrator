using FlowOrchestrator.Dashboard.Webhooks;

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

    /// <summary>
    /// Identity-based access through the host's ASP.NET Core authentication (OpenID Connect / Keycloak),
    /// with viewer and operator roles. Works alongside <see cref="BasicAuth"/>; disabled until
    /// <see cref="FlowDashboardAuthorizationOptions.AuthenticationScheme"/> is set.
    /// </summary>
    public FlowDashboardAuthorizationOptions Authorization { get; set; } = new();

    /// <summary>
    /// Extra origins (e.g. <c>"https://flows.example.com"</c>) allowed to make state-changing dashboard calls.
    /// Only needed behind a reverse proxy that rewrites <c>Host</c>, serves plain HTTP, and does not send
    /// ASP.NET Core ForwardedHeaders middleware restoring it.
    /// </summary>
    /// <remarks>
    /// State-changing calls (trigger, rerun, retry, cancel, signal, schedule changes) are rejected when the
    /// browser marks them cross-site. Browsers send <c>Sec-Fetch-Site</c> only to HTTPS and localhost; over
    /// plain HTTP the dashboard compares <c>Origin</c> with <c>Host</c>, and a proxy
    /// that rewrites <c>Host</c> to its upstream address makes the dashboard's own calls look cross-site.
    /// </remarks>
    public string[]? TrustedOrigins { get; set; }

    /// <summary>UI branding settings (title, subtitle, logo URL).</summary>
    public FlowDashboardBrandingOptions Branding { get; set; } = new();

    /// <summary>
    /// Enterprise webhook hardening pipeline (HMAC signature verification, replay
    /// protection, rate limiting, body-size cap, DLQ). Defaults to
    /// <see cref="WebhookEnforcementMode.Off"/>; set
    /// <c>UseWebhookSecurity(...)</c> to opt in.
    /// </summary>
    public WebhookSecurityOptions WebhookSecurity { get; set; } = new();

    /// <summary>
    /// Time window applied to a free-text search (run list and webhook deliveries) that does not
    /// bound it itself. Defaults to 7 days.
    /// </summary>
    /// <remarks>
    /// A free-text term cannot use an index (it is a <c>%term%</c> match), so without a window it reads
    /// every row in retention; the window turns that into a range seek on the time index. It is not
    /// applied to a run-id lookup or to a term recognised as a status name, which are already index
    /// seeks. Callers widen it per request with the <c>from</c> / <c>to</c> query parameters. A zero or
    /// negative value falls back to the 7-day default: the window is what keeps the opt-in
    /// step-payload search bounded, so it cannot be switched off here.
    /// Bind from configuration as a <see cref="TimeSpan"/> string, e.g. <c>"30.00:00:00"</c>.
    /// </remarks>
    public TimeSpan RunSearchWindow { get; set; } = DefaultRunSearchWindow;

    /// <summary>Default for <see cref="RunSearchWindow"/>: 7 days.</summary>
    public static readonly TimeSpan DefaultRunSearchWindow = TimeSpan.FromDays(7);

    /// <summary><see cref="RunSearchWindow"/> with non-positive values replaced by <see cref="DefaultRunSearchWindow"/>.</summary>
    internal TimeSpan EffectiveRunSearchWindow => RunSearchWindow > TimeSpan.Zero ? RunSearchWindow : DefaultRunSearchWindow;

    /// <summary>
    /// Configures the webhook hardening pipeline through a fluent builder.
    /// </summary>
    /// <param name="configure">Configuration callback receiving a <see cref="WebhookSecurityOptionsBuilder"/>.</param>
    public FlowDashboardOptions UseWebhookSecurity(Action<WebhookSecurityOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(new WebhookSecurityOptionsBuilder(WebhookSecurity));
        return this;
    }
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
