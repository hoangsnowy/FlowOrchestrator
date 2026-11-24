using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Dashboard.Security;

/// <summary>Who is using the dashboard and what they may do, resolved once per request.</summary>
/// <param name="Method">
/// <c>"none"</c> (no protection configured), <c>"basic"</c>, or <c>"oidc"</c> for any identity signed in through
/// <see cref="FlowDashboardAuthorizationOptions"/> (the cookie scheme or an additional scheme such as a bearer token).
/// </param>
/// <param name="UserName">Display name of the signed-in user; <see langword="null"/> when there is none.</param>
/// <param name="CanOperate"><see langword="true"/> when the caller may use state-changing endpoints.</param>
/// <param name="CanView">
/// <see langword="true"/> when the caller may read runs. Only an identity with neither a viewer nor an operator
/// role is <see langword="false"/>; such a caller reaches nothing but <c>/api/me</c>, <c>/signout</c> and the
/// signal endpoint, where a per-signal restriction may still let it in.
/// </param>
internal sealed record DashboardAccess(string Method, string? UserName, bool CanOperate, bool CanView = true)
{
    /// <summary><see langword="true"/> when the caller is an identity signed in through <see cref="FlowDashboardAuthorizationOptions"/>.</summary>
    public bool IsIdentity => Method == "oidc";

    /// <summary><see cref="HttpContext.Items"/> key the filter stores the resolved access under.</summary>
    public const string ItemKey = "FlowOrchestrator.Dashboard.Access";

    /// <summary>
    /// Access granted when neither Basic Auth nor identity-based access is configured, in the Development
    /// environment or when <see cref="FlowDashboardOptions.AllowAnonymousOperators"/> is set.
    /// </summary>
    public static readonly DashboardAccess Unprotected = new("none", null, CanOperate: true);

    /// <summary>
    /// Access granted when neither Basic Auth nor identity-based access is configured outside the Development
    /// environment: anonymous callers may read (with secrets masked) but not change state.
    /// </summary>
    public static readonly DashboardAccess UnprotectedReadOnly = new("none", null, CanOperate: false);

    /// <summary>
    /// Returns the access anonymous callers get when the dashboard has no authentication configured.
    /// </summary>
    /// <param name="options">The dashboard options; <see cref="FlowDashboardOptions.AllowAnonymousOperators"/> opts back in to operator access.</param>
    /// <param name="isDevelopment">
    /// <see langword="true"/> when the host runs in the Development environment. A host that registers no
    /// <c>IHostEnvironment</c> is treated as non-Development.
    /// </param>
    /// <returns><see cref="Unprotected"/> in Development or when opted in; otherwise <see cref="UnprotectedReadOnly"/>.</returns>
    public static DashboardAccess ForAnonymous(FlowDashboardOptions options, bool isDevelopment)
        => isDevelopment || options.AllowAnonymousOperators ? Unprotected : UnprotectedReadOnly;
}

/// <summary>
/// Gates every dashboard route (page, REST API, event stream) with Basic Auth and / or the host's
/// ASP.NET Core authentication, side by side.
/// </summary>
/// <remarks>
/// <para>Order: valid Basic credentials win and grant full access, which keeps scripts, probes and existing
/// deployments working unchanged. Otherwise, when <see cref="FlowDashboardAuthorizationOptions"/> is
/// enabled, the configured scheme authenticates the request and the user's roles (or policies) decide
/// between view and operate. A browser opening the page without a session is challenged (redirected to the
/// identity provider); an API or event-stream call gets a plain 401 so <c>fetch</c> never pops a Basic
/// dialog.</para>
/// <para>State-changing endpoints are identified by HTTP method: every dashboard <c>POST</c> / <c>PUT</c> /
/// <c>PATCH</c> / <c>DELETE</c> route mutates (trigger, rerun, retry, cancel, signal, schedule changes), every
/// <c>GET</c> reads. Webhook receive endpoints are mapped outside the dashboard group and never reach this
/// filter. The one exception to "mutating needs operator" is an endpoint marked with
/// <see cref="PerSignalAuthorizationMetadata"/> (the signal endpoint): a signed-in identity reaches it whatever
/// its role, and the endpoint applies the signal's own restriction or, for an unrestricted signal, the operator
/// rule.</para>
/// <para>Identities come from <see cref="FlowDashboardAuthorizationOptions.AuthenticationScheme"/> and then each
/// of <see cref="FlowDashboardAuthorizationOptions.AdditionalAuthenticationSchemes"/>. A request with a non-Basic
/// <c>Authorization</c> header that none of them accepts gets <c>401</c>, never the identity-provider redirect.</para>
/// <para>With neither configured, anonymous callers get the access resolved at startup by
/// <see cref="DashboardAccess.ForAnonymous"/>: full access in Development (or with
/// <see cref="FlowDashboardOptions.AllowAnonymousOperators"/>), read-only with secrets masked everywhere
/// else.</para>
/// </remarks>
/// <param name="options">The dashboard options.</param>
/// <param name="basePath">The dashboard's route prefix.</param>
/// <param name="anonymousAccess">
/// What a caller may do when no authentication is configured; defaults to
/// <see cref="DashboardAccess.UnprotectedReadOnly"/>.
/// </param>
internal sealed class DashboardAccessFilter(FlowDashboardOptions options, string basePath, DashboardAccess? anonymousAccess = null) : IEndpointFilter
{
    /// <summary>Body of the 403 a state-changing call gets on a dashboard that is read-only because it has no authentication.</summary>
    internal const string AnonymousReadOnlyMessage =
        "The flow dashboard has no authentication configured, so it is read-only outside the Development environment. "
        + "Configure FlowDashboard:BasicAuth or FlowDashboard:Authorization, or set FlowDashboard:AllowAnonymousOperators to true.";

    private readonly DashboardAccess _anonymousAccess = anonymousAccess ?? DashboardAccess.UnprotectedReadOnly;
    // Read once: the option getter allocates, and this filter runs on every dashboard request.
    private readonly IReadOnlyList<string> _additionalSchemes = options.Authorization.EffectiveAdditionalSchemes;
    private readonly string _apiPrefix = basePath.TrimEnd('/') + "/api/";
    private readonly string _mePath = basePath.TrimEnd('/') + "/api/me";
    private readonly string _signOutPath = basePath.TrimEnd('/') + "/signout";

    /// <inheritdoc/>
    /// <remarks>
    /// Synchronous for the common cases (unprotected, valid Basic, Basic challenge): this runs on every
    /// dashboard request including the 5 s poll, so it returns <paramref name="next"/>'s ValueTask directly
    /// instead of adding an async state machine. Unprotected requests with operator access leave
    /// <see cref="HttpContext.Items"/> alone; readers fall back to <see cref="DashboardAccess.Unprotected"/>.
    /// Read-only anonymous access is recorded explicitly so every reader sees it.
    /// </remarks>
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var basic = options.BasicAuth;
        var identity = options.Authorization;

        // CSRF: a cookie session (and a browser-cached Basic credential) rides along on a cross-site form
        // post, so state-changing calls must come from the dashboard's own origin. Scripts that send
        // neither header (curl, probes) are unaffected.
        if (IsMutating(http.Request.Method) && IsCrossSiteRequest(http, options.TrustedOrigins))
            return ErrorAsync(http, StatusCodes.Status403Forbidden, "Cross-site request blocked: state-changing dashboard calls must come from the dashboard itself.");

        if (!basic.IsEnabled && !identity.IsEnabled)
        {
            if (_anonymousAccess.CanOperate)
                return next(context);

            http.Items[DashboardAccess.ItemKey] = _anonymousAccess;
            if (IsMutating(http.Request.Method))
                return ErrorAsync(http, StatusCodes.Status403Forbidden, AnonymousReadOnlyMessage);
            return next(context);
        }

        if (basic.IsEnabled && TryValidateBasic(http, basic, out var basicUser))
        {
            http.Items[DashboardAccess.ItemKey] = new DashboardAccess("basic", basicUser, CanOperate: true);
            return next(context);
        }

        if (!identity.IsEnabled)
            return ValueTask.FromResult(BasicChallenge(http, basic));

        return AuthenticateAndInvokeAsync(context, next);
    }

    private async ValueTask<object?> AuthenticateAndInvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var basic = options.BasicAuth;
        var identity = options.Authorization;

        var principal = await AuthenticateAsync(http, identity.AuthenticationScheme!);
        if (principal is not null)
        {
            http.User = principal;
            var canOperate = await HasAccessAsync(http, principal, identity.OperatePolicy, identity.EffectiveOperatorRoles);
            var canView = canOperate || await HasAccessAsync(http, principal, identity.ViewPolicy, identity.EffectiveViewerRoles);
            http.Items[DashboardAccess.ItemKey] = new DashboardAccess("oidc", DisplayName(principal), canOperate, canView);

            var needsOperate = IsMutating(http.Request.Method);
            // /api/me and /signout stay reachable for any signed-in user, so one without a dashboard role
            // can still see why and switch accounts. The signal endpoint authorizes per signal: a restricted
            // signal admits its approvers whatever their dashboard role, so it is the endpoint's call, not ours.
            if (needsOperate ? canOperate || IsPerSignalAuthorized(http) : (canView || IsAccountEndpoint(http)))
                return await next(context);

            return await ErrorAsync(http, StatusCodes.Status403Forbidden, needsOperate
                ? "Your account has view-only access to the flow dashboard. This action needs an operator role."
                : "Your account has no access to the flow dashboard.");
        }

        // A caller that presented its own credential (a bearer token, …) and was not accepted is a client, not
        // a browser that lost its session: answer 401 instead of redirecting it to the identity provider.
        var authorizationHeader = http.Request.Headers.Authorization.ToString();
        var presentedToken = authorizationHeader.Length > 0 && !authorizationHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase);
        if (presentedToken && _additionalSchemes.Count > 0)
        {
            // Each scheme writes its own challenge (a JWT bearer handler: 401 + WWW-Authenticate with the reason).
            foreach (var scheme in _additionalSchemes)
                await http.ChallengeAsync(scheme);
            return Results.Empty;
        }

        if (!presentedToken && IsPageNavigation(http))
        {
            await http.ChallengeAsync(identity.EffectiveChallengeScheme);
            return Results.Empty;
        }

        // A wrong Basic header on an API call still gets the Basic challenge it asked for; a session-less
        // fetch / EventSource gets a bare 401 the SPA turns into "sign in again".
        if (basic.IsEnabled && http.Request.Headers.Authorization.ToString().StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return BasicChallenge(http, basic);

        return await ErrorAsync(http, StatusCodes.Status401Unauthorized, "Sign in to the flow dashboard.");
    }

    /// <summary>
    /// Authenticates with <paramref name="primaryScheme"/>, then each additional scheme in order, and returns the
    /// first principal any of them produces, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The common case — a browser with a cookie — succeeds on the first scheme and never touches the others;
    /// additional schemes cost one handler call each only for requests the cookie scheme did not accept.
    /// </remarks>
    private async ValueTask<ClaimsPrincipal?> AuthenticateAsync(HttpContext http, string primaryScheme)
    {
        var result = await http.AuthenticateAsync(primaryScheme);
        if (result.Succeeded && result.Principal is { } principal)
            return principal;

        foreach (var scheme in _additionalSchemes)
        {
            result = await http.AuthenticateAsync(scheme);
            if (result.Succeeded && result.Principal is { } additional)
                return additional;
        }

        return null;
    }

    /// <summary>
    /// <see langword="true"/> for an endpoint that authorizes each request itself (the signal endpoint), marked
    /// with <see cref="PerSignalAuthorizationMetadata"/>.
    /// </summary>
    private static bool IsPerSignalAuthorized(HttpContext http)
        => http.GetEndpoint()?.Metadata.GetMetadata<PerSignalAuthorizationMetadata>() is not null;

    // Written as bytes rather than Results.Json: the System.Text.Json PipeWriter path throws on hosts whose
    // response PipeWriter lacks UnflushedBytes (TestServer, some proxies). Same reason as WriteJsonAsync.
    private static async ValueTask<object?> ErrorAsync(HttpContext http, int statusCode, string message)
    {
        http.Response.StatusCode = statusCode;
        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { error = message }));
        return Results.Empty;
    }

    /// <summary><see langword="true"/> for methods that change state.</summary>
    internal static bool IsMutating(string method)
        => !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));

    private bool IsPageNavigation(HttpContext http)
        => HttpMethods.IsGet(http.Request.Method)
           && !(http.Request.Path.Value ?? string.Empty).StartsWith(_apiPrefix, StringComparison.OrdinalIgnoreCase);

    private bool IsAccountEndpoint(HttpContext http)
    {
        var path = http.Request.Path.Value ?? string.Empty;
        return string.Equals(path, _mePath, StringComparison.OrdinalIgnoreCase)
               || string.Equals(path, _signOutPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <see langword="true"/> when a browser says the request comes from another origin: <c>Sec-Fetch-Site</c>
    /// other than <c>same-origin</c> / <c>none</c>, or, from browsers without fetch metadata, an
    /// <c>Origin</c> whose host differs from the request host.
    /// </summary>
    /// <remarks>
    /// <c>Sec-Fetch-Site</c> is preferred because the browser computes it, so it stays correct behind a
    /// reverse proxy that rewrites <c>Host</c>. A request with neither header (a script) is not cross-site.
    /// </remarks>
    internal static bool IsCrossSiteRequest(HttpContext http, IReadOnlyList<string>? trustedOrigins = null)
    {
        var fetchSite = http.Request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length > 0)
            return !(string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(fetchSite, "none", StringComparison.OrdinalIgnoreCase));

        var origin = http.Request.Headers.Origin.ToString();
        if (origin.Length == 0)
            return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            return true;   // "null" (sandboxed iframe, file:) or garbage
        if (string.Equals(originUri.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
            return false;
        // Behind a proxy that rewrites Host over plain HTTP (no Sec-Fetch-Site), either let ASP.NET Core's
        // ForwardedHeaders middleware restore Request.Host, or list the public origin in TrustedOrigins.
        // X-Forwarded-Host is deliberately not read here: a host with permissive credentialed CORS would let
        // a cross-site page set it.
        if (trustedOrigins is not null)
        {
            foreach (var trusted in trustedOrigins)
            {
                if (Uri.TryCreate(trusted, UriKind.Absolute, out var trustedUri)
                    && string.Equals(trustedUri.Scheme, originUri.Scheme, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(trustedUri.Authority, originUri.Authority, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }
        return true;
    }

    private static async ValueTask<bool> HasAccessAsync(HttpContext http, ClaimsPrincipal principal, string? policy, IReadOnlyList<string> roles)
    {
        if (!string.IsNullOrWhiteSpace(policy))
        {
            var authorization = http.RequestServices.GetService<IAuthorizationService>();
            return authorization is not null && (await authorization.AuthorizeAsync(principal, policy)).Succeeded;
        }

        return roles.Count == 0 || DashboardRoles.HasAnyRole(principal, roles);
    }

    /// <summary>Name the dashboard shows for, and audits, a signed-in user.</summary>
    /// <param name="principal">The signed-in user.</param>
    /// <returns>The first of <c>preferred_username</c>, the identity name, <c>name</c>, e-mail and subject; or <see langword="null"/>.</returns>
    /// <remarks>A Keycloak client-credentials token names the service account in <c>preferred_username</c>
    /// (<c>service-account-&lt;client&gt;</c>), so machine callers are audited by client too.</remarks>
    internal static string? DisplayName(ClaimsPrincipal principal)
        => principal.FindFirst("preferred_username")?.Value
           ?? principal.Identity?.Name
           ?? principal.FindFirst("name")?.Value
           ?? principal.FindFirst(ClaimTypes.Email)?.Value
           ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    private static object? BasicChallenge(HttpContext http, FlowDashboardBasicAuthOptions basic)
    {
        var realm = string.IsNullOrWhiteSpace(basic.Realm)
            ? "FlowOrchestrator Dashboard"
            : basic.Realm.Replace("\"", string.Empty, StringComparison.Ordinal);
        http.Response.Headers.WWWAuthenticate = $"Basic realm=\"{realm}\", charset=\"UTF-8\"";
        return Results.Unauthorized();
    }

    /// <summary>Checks an <c>Authorization: Basic</c> header against the configured credentials in constant time.</summary>
    /// <param name="http">The request.</param>
    /// <param name="basic">Configured credentials.</param>
    /// <param name="userName">The authenticated user name when the credentials match.</param>
    /// <returns><see langword="true"/> when the header carries the configured username and password.</returns>
    internal static bool TryValidateBasic(HttpContext http, FlowDashboardBasicAuthOptions basic, out string? userName)
    {
        userName = null;
        if (!basic.IsEnabled)
            return false;

        const string basicPrefix = "Basic ";
        var headerValue = http.Request.Headers.Authorization.ToString();
        if (!headerValue.StartsWith(basicPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var encoded = headerValue[basicPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(encoded))
            return false;

        string userAndPassword;
        try
        {
            userAndPassword = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return false;
        }

        var separatorIndex = userAndPassword.IndexOf(':');
        if (separatorIndex <= 0)
            return false;

        var user = userAndPassword[..separatorIndex];
        var password = userAndPassword[(separatorIndex + 1)..];
        if (!SecureEquals(user, basic.Username!) || !SecureEquals(password, basic.Password!))
            return false;

        userName = user;
        return true;
    }

    private static bool SecureEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
}

/// <summary>
/// Endpoint metadata telling <see cref="DashboardAccessFilter"/> that the endpoint authorizes each request
/// itself, so a signed-in caller without the operator role is passed through to it instead of refused.
/// </summary>
/// <remarks>
/// Only the signal endpoint carries it: a restricted signal admits the identities its step names, whatever
/// their dashboard role, and an unrestricted one still demands the operator role, which the endpoint checks.
/// Authentication, the CSRF check and the Basic / anonymous rules still run in the filter first.
/// </remarks>
internal sealed class PerSignalAuthorizationMetadata
{
    /// <summary>The shared instance; the marker carries no state.</summary>
    public static readonly PerSignalAuthorizationMetadata Instance = new();

    private PerSignalAuthorizationMetadata()
    {
    }
}
