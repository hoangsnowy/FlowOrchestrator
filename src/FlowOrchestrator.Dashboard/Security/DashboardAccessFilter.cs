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
/// <param name="Method"><c>"none"</c> (no protection configured), <c>"basic"</c> or <c>"oidc"</c>.</param>
/// <param name="UserName">Display name of the signed-in user; <see langword="null"/> when there is none.</param>
/// <param name="CanOperate"><see langword="true"/> when the caller may use state-changing endpoints.</param>
internal sealed record DashboardAccess(string Method, string? UserName, bool CanOperate)
{
    /// <summary><see cref="HttpContext.Items"/> key the filter stores the resolved access under.</summary>
    public const string ItemKey = "FlowOrchestrator.Dashboard.Access";

    /// <summary>Access granted when neither Basic Auth nor identity-based access is configured.</summary>
    public static readonly DashboardAccess Unprotected = new("none", null, CanOperate: true);
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
/// filter.</para>
/// </remarks>
internal sealed class DashboardAccessFilter(FlowDashboardOptions options, string basePath) : IEndpointFilter
{
    private static readonly string[] RoleClaimTypes = [ClaimTypes.Role, "role", "roles"];
    private readonly string _apiPrefix = basePath.TrimEnd('/') + "/api/";
    private readonly string _mePath = basePath.TrimEnd('/') + "/api/me";
    private readonly string _signOutPath = basePath.TrimEnd('/') + "/signout";

    /// <inheritdoc/>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var basic = options.BasicAuth;
        var identity = options.Authorization;

        // CSRF: a cookie session (and a browser-cached Basic credential) rides along on a cross-site form
        // post, so state-changing calls must come from the dashboard's own origin. Scripts that send
        // neither header (curl, probes) are unaffected.
        if (IsMutating(http.Request.Method) && IsCrossSiteRequest(http))
            return await ErrorAsync(http, StatusCodes.Status403Forbidden, "Cross-site request blocked: state-changing dashboard calls must come from the dashboard itself.");

        if (!basic.IsEnabled && !identity.IsEnabled)
        {
            http.Items[DashboardAccess.ItemKey] = DashboardAccess.Unprotected;
            return await next(context);
        }

        if (basic.IsEnabled && TryValidateBasic(http, basic, out var basicUser))
        {
            http.Items[DashboardAccess.ItemKey] = new DashboardAccess("basic", basicUser, CanOperate: true);
            return await next(context);
        }

        if (!identity.IsEnabled)
            return BasicChallenge(http, basic);

        var result = await http.AuthenticateAsync(identity.AuthenticationScheme);
        if (result.Succeeded && result.Principal is { } principal)
        {
            http.User = principal;
            var canOperate = await HasAccessAsync(http, principal, identity.OperatePolicy, identity.EffectiveOperatorRoles);
            var canView = canOperate || await HasAccessAsync(http, principal, identity.ViewPolicy, identity.EffectiveViewerRoles);
            http.Items[DashboardAccess.ItemKey] = new DashboardAccess("oidc", DisplayName(principal), canOperate);

            var needsOperate = IsMutating(http.Request.Method);
            // /api/me and /signout stay reachable for any signed-in user, so one without a dashboard role
            // can still see why and switch accounts.
            if (needsOperate ? canOperate : (canView || IsAccountEndpoint(http)))
                return await next(context);

            return await ErrorAsync(http, StatusCodes.Status403Forbidden, needsOperate
                ? "Your account has view-only access to the flow dashboard. This action needs an operator role."
                : "Your account has no access to the flow dashboard.");
        }

        if (IsPageNavigation(http))
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
    internal static bool IsCrossSiteRequest(HttpContext http)
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
        return !string.Equals(originUri.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static async ValueTask<bool> HasAccessAsync(HttpContext http, ClaimsPrincipal principal, string? policy, IReadOnlyList<string> roles)
    {
        if (!string.IsNullOrWhiteSpace(policy))
        {
            var authorization = http.RequestServices.GetService<IAuthorizationService>();
            return authorization is not null && (await authorization.AuthorizeAsync(principal, policy)).Succeeded;
        }

        if (roles.Count == 0)
            return true;

        foreach (var role in roles)
        {
            if (principal.IsInRole(role))
                return true;
        }

        // OIDC handlers keep a JSON "roles" array as claims of type "roles", which IsInRole does not read.
        foreach (var claim in principal.Claims)
        {
            if (Array.IndexOf(RoleClaimTypes, claim.Type) >= 0 && Contains(roles, claim.Value))
                return true;
        }

        return false;
    }

    private static bool Contains(IReadOnlyList<string> roles, string value)
    {
        foreach (var role in roles)
        {
            if (string.Equals(role, value, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string? DisplayName(ClaimsPrincipal principal)
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
