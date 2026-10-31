namespace FlowOrchestrator.Dashboard;

/// <summary>
/// Signs dashboard users in through the host's ASP.NET Core authentication (OpenID Connect against
/// Keycloak, Entra ID, Auth0, …) and splits them into viewers and operators. Bound from
/// <c>FlowDashboard:Authorization</c>.
/// </summary>
/// <remarks>
/// <para>Runs <b>alongside</b> <see cref="FlowDashboardBasicAuthOptions"/>: a request carrying valid Basic
/// credentials is let through as an operator exactly as before, so scripts, health probes and the e2e
/// suite keep working, while a browser without credentials is redirected to the identity provider.</para>
/// <para>The dashboard does not register any authentication handler itself. The host registers the
/// schemes (for example <c>AddCookie()</c> + <c>AddOpenIdConnect("oidc", …)</c>) and names them here.
/// Webhook receive endpoints are never challenged; they keep their webhook-secret / HMAC protection.</para>
/// </remarks>
public sealed class FlowDashboardAuthorizationOptions
{
    /// <summary>
    /// Scheme that authenticates a dashboard request, typically the cookie scheme the OIDC handler signs
    /// into (<c>"Cookies"</c>), or a bearer scheme for API-only use. <see langword="null"/> disables
    /// identity-based access.
    /// </summary>
    public string? AuthenticationScheme { get; set; }

    /// <summary>
    /// Scheme challenged when a browser opens the dashboard without a session, typically the OIDC scheme
    /// (<c>"oidc"</c>), which redirects to the identity provider. Defaults to
    /// <see cref="AuthenticationScheme"/>.
    /// </summary>
    public string? ChallengeScheme { get; set; }

    /// <summary>
    /// Roles allowed to read the dashboard: pages, run lists, run detail, the event stream. A user with any
    /// of them, or any <see cref="OperatorRoles"/> role, can view. <see langword="null"/> means
    /// <c>flow-viewer</c>; an empty array means any signed-in user can view.
    /// </summary>
    /// <remarks>Nullable on purpose: the configuration binder appends to an initialised array instead of
    /// replacing it, so a default here could never be overridden from <c>appsettings.json</c>.</remarks>
    public string[]? ViewerRoles { get; set; }

    /// <summary>
    /// Roles allowed to change state: trigger, enable / disable, rerun, retry, cancel, send signals, pause /
    /// resume schedules and override cron. <see langword="null"/> means <c>flow-operator</c>; an empty array
    /// means any signed-in user can operate.
    /// </summary>
    public string[]? OperatorRoles { get; set; }

    /// <summary>
    /// Authorization policy that grants view access. When set it replaces the <see cref="ViewerRoles"/>
    /// check; the host registers it with <c>AddAuthorization</c>.
    /// </summary>
    public string? ViewPolicy { get; set; }

    /// <summary>
    /// Authorization policy that grants operate access. When set it replaces the
    /// <see cref="OperatorRoles"/> check.
    /// </summary>
    public string? OperatePolicy { get; set; }

    /// <summary><see langword="true"/> when <see cref="AuthenticationScheme"/> is configured.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(AuthenticationScheme);

    /// <summary><see cref="ChallengeScheme"/>, falling back to <see cref="AuthenticationScheme"/>.</summary>
    internal string? EffectiveChallengeScheme => string.IsNullOrWhiteSpace(ChallengeScheme) ? AuthenticationScheme : ChallengeScheme;

    /// <summary>Viewer roles with the default applied.</summary>
    internal IReadOnlyList<string> EffectiveViewerRoles => ViewerRoles ?? ["flow-viewer"];

    /// <summary>Operator roles with the default applied.</summary>
    internal IReadOnlyList<string> EffectiveOperatorRoles => OperatorRoles ?? ["flow-operator"];
}
