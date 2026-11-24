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
    /// Extra schemes that authenticate dashboard requests alongside <see cref="AuthenticationScheme"/>, tried
    /// in order when it does not, for example <c>"Bearer"</c> from <c>AddJwtBearer</c> so machine clients
    /// (a Keycloak service account using client credentials) can call the REST API with an access token.
    /// <see langword="null"/> or empty means none.
    /// </summary>
    /// <remarks>
    /// <para>The same viewer / operator roles, policies and per-signal restrictions apply whichever scheme
    /// signed the caller in. A request that carries an <c>Authorization</c> header other than Basic and that no
    /// scheme accepts gets <c>401</c> — each of these schemes is challenged, so a JWT bearer handler adds its
    /// <c>WWW-Authenticate</c> header — and is never redirected to the identity provider.</para>
    /// <para>These schemes take no part in <c>/signout</c>: a bearer token has no server-side session to end.
    /// Every name must be registered with <c>AddAuthentication()</c>, and <see cref="AuthenticationScheme"/>
    /// must be set; otherwise the dashboard fails when it maps its endpoints.</para>
    /// <para>Nullable for the same reason as <see cref="ViewerRoles"/>.</para>
    /// </remarks>
    public string[]? AdditionalAuthenticationSchemes { get; set; }

    /// <summary>
    /// What the signal endpoint does with a run whose flow is not registered in this host, so its
    /// <c>WaitForSignal</c> restrictions cannot be read. Defaults to <see cref="UnregisteredFlowSignalMode.Deny"/>.
    /// </summary>
    /// <remarks>
    /// Applies whatever authentication is configured, anonymous Development access included. A deployment that
    /// serves the dashboard from a host without the flows (workers elsewhere) either registers the same flows on
    /// the dashboard host or opts in to <see cref="UnregisteredFlowSignalMode.OperatorRule"/>, which is logged at
    /// startup because it lets any operator deliver a signal a restriction would have refused.
    /// </remarks>
    public UnregisteredFlowSignalMode UnregisteredFlowSignals { get; set; } = UnregisteredFlowSignalMode.Deny;

    /// <summary>
    /// Roles allowed to read the dashboard: pages, run lists, run detail, the event stream. A user with any
    /// of them, or any <see cref="OperatorRoles"/> role, can view. <see langword="null"/> means
    /// <c>flow-viewer</c>; an empty array means any signed-in user can view.
    /// </summary>
    /// <remarks>Nullable on purpose: the configuration binder appends to an initialised array instead of
    /// replacing it, so a default here could never be overridden from <c>appsettings.json</c>.</remarks>
    public string[]? ViewerRoles { get; set; }

    /// <summary>
    /// Roles allowed to change state: trigger, enable / disable, rerun, retry, cancel, send unrestricted signals, pause /
    /// resume schedules and override cron. <see langword="null"/> means <c>flow-operator</c>; an empty array
    /// means any signed-in user can operate.
    /// </summary>
    /// <remarks>A <i>restricted</i> signal — its <c>WaitForSignal</c> step declares <c>allowedRoles</c> or
    /// <c>authorizationPolicy</c> — is authorized by that restriction alone: these roles neither grant nor are
    /// needed for it.</remarks>
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

    /// <summary>
    /// <see cref="AdditionalAuthenticationSchemes"/> trimmed, without blanks, duplicates or a repeat of
    /// <see cref="AuthenticationScheme"/>; empty when none are set. Allocates — read it once, not per request.
    /// </summary>
    internal IReadOnlyList<string> EffectiveAdditionalSchemes
    {
        get
        {
            if (AdditionalAuthenticationSchemes is not { Length: > 0 } configured)
                return [];
            var schemes = new List<string>(configured.Length);
            foreach (var scheme in configured.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()))
            {
                if (!string.Equals(scheme, AuthenticationScheme, StringComparison.Ordinal) && !schemes.Contains(scheme, StringComparer.Ordinal))
                    schemes.Add(scheme);
            }
            return schemes;
        }
    }

    /// <summary>Operator roles with the default applied.</summary>
    internal IReadOnlyList<string> EffectiveOperatorRoles => OperatorRoles ?? ["flow-operator"];
}

/// <summary>
/// How the dashboard signal endpoint treats a run whose flow the dashboard host does not register.
/// </summary>
public enum UnregisteredFlowSignalMode
{
    /// <summary>
    /// Refuse the delivery with <c>403</c> for every caller: without the manifest the dashboard cannot tell
    /// whether the signal is restricted, so it fails closed. The default.
    /// </summary>
    Deny = 0,

    /// <summary>
    /// Treat the signal as unrestricted: an operator may deliver it. Only for split deployments whose
    /// dashboard host deliberately does not register the flows; a restriction declared on the step is then not
    /// enforced over HTTP.
    /// </summary>
    OperatorRule = 1,
}
