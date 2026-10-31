using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FlowOrchestrator.Core.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Locks down dashboard access with Basic Auth and identity-based sign-in (OIDC / Keycloak) side by side:
/// who may view, who may operate, how an anonymous browser vs. an API call is challenged, and that webhook
/// receive endpoints are never challenged. A header-driven test scheme stands in for the cookie the OIDC
/// handler would issue.
/// </summary>
public sealed class DashboardAuthorizationTests
{
    private const string Scheme = "Test";
    private static readonly string BasicHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:correct-horse-battery"));

    private static DashboardTestServer Server(bool basic, bool identity, Action<FlowDashboardOptions>? configure = null, Action<IServiceCollection>? services = null)
        => DashboardTestServer.WithServices(s =>
        {
            s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(Scheme, _ => { });
            services?.Invoke(s);
            s.AddFlowDashboard(o =>
            {
                if (basic)
                {
                    o.BasicAuth.Username = "admin";
                    o.BasicAuth.Password = "correct-horse-battery";
                }
                if (identity)
                    o.Authorization.AuthenticationScheme = Scheme;
                configure?.Invoke(o);
            });
        });

    private static HttpRequestMessage Request(HttpMethod method, string path, string? user = null, string? roles = null, string? realmAccess = null, bool basic = false, bool html = false)
    {
        var request = new HttpRequestMessage(method, path);
        if (user is not null) request.Headers.Add("X-Test-User", user);
        if (roles is not null) request.Headers.Add("X-Test-Roles", roles);
        if (realmAccess is not null) request.Headers.Add("X-Test-Realm-Access", realmAccess);
        if (basic) request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicHeader);
        if (html) request.Headers.Accept.ParseAdd("text/html");
        if (method != HttpMethod.Get) request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<JsonElement> MeAsync(HttpClient client, HttpRequestMessage request)
    {
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Unprotected_dashboard_reports_no_auth_and_full_access()
    {
        // Arrange
        using var server = new DashboardTestServer();

        // Act
        var me = await MeAsync(server.Client, Request(HttpMethod.Get, "/flows/api/me"));

        // Assert
        Assert.Equal("none", me.GetProperty("method").GetString());
        Assert.True(me.GetProperty("canOperate").GetBoolean());
        Assert.False(me.GetProperty("canSignOut").GetBoolean());
    }

    [Fact]
    public async Task Anonymous_browser_is_challenged_and_anonymous_api_call_gets_a_bare_401()
    {
        // Arrange
        using var server = Server(basic: false, identity: true);
        var client = server.CreateClient();

        // Act
        var page = await client.SendAsync(Request(HttpMethod.Get, "/flows", html: true));
        var api = await client.SendAsync(Request(HttpMethod.Get, "/flows/api/runs"));
        var stream = await client.SendAsync(Request(HttpMethod.Get, "/flows/api/events/stream"));

        // Assert — the page goes to the identity provider; fetch / EventSource never get a Basic dialog.
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/test-login", page.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Empty(api.Headers.WwwAuthenticate);
        Assert.Equal(HttpStatusCode.Unauthorized, stream.StatusCode);
    }

    [Fact]
    public async Task Viewer_can_read_but_gets_403_on_every_state_changing_endpoint()
    {
        // Arrange
        using var server = Server(basic: false, identity: true);
        var client = server.CreateClient();
        var runId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var mutations = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Post, $"/flows/api/flows/{flowId}/trigger"),
            (HttpMethod.Post, $"/flows/api/flows/{flowId}/enable"),
            (HttpMethod.Post, $"/flows/api/flows/{flowId}/disable"),
            (HttpMethod.Post, $"/flows/api/runs/{runId}/cancel"),
            (HttpMethod.Post, $"/flows/api/runs/{runId}/rerun"),
            (HttpMethod.Post, $"/flows/api/runs/{runId}/steps/s1/retry"),
            (HttpMethod.Post, $"/flows/api/runs/{runId}/signals/approval"),
            (HttpMethod.Post, "/flows/api/schedules/job-1/trigger"),
            (HttpMethod.Post, "/flows/api/schedules/job-1/pause"),
            (HttpMethod.Post, "/flows/api/schedules/job-1/resume"),
            (HttpMethod.Put, "/flows/api/schedules/job-1/cron"),
        };

        // Act
        var read = await client.SendAsync(Request(HttpMethod.Get, "/flows/api/runs", user: "vera", roles: "flow-viewer"));
        var page = await client.SendAsync(Request(HttpMethod.Get, "/flows", user: "vera", roles: "flow-viewer", html: true));
        var writes = new List<HttpStatusCode>();
        foreach (var (method, path) in mutations)
            writes.Add((await client.SendAsync(Request(method, path, user: "vera", roles: "flow-viewer"))).StatusCode);
        var me = await MeAsync(client, Request(HttpMethod.Get, "/flows/api/me", user: "vera", roles: "flow-viewer"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.All(writes, code => Assert.Equal(HttpStatusCode.Forbidden, code));
        Assert.Equal("oidc", me.GetProperty("method").GetString());
        Assert.Equal("vera", me.GetProperty("user").GetString());
        Assert.False(me.GetProperty("canOperate").GetBoolean());
        Assert.True(me.GetProperty("canSignOut").GetBoolean());
    }

    [Fact]
    public async Task Operator_passes_the_gate_on_state_changing_endpoints()
    {
        // Arrange
        using var server = Server(basic: false, identity: true);
        var runId = Guid.NewGuid();

        // Act — the run does not exist, so getting past the gate means the endpoint answers 404.
        var cancel = await server.Client.SendAsync(Request(HttpMethod.Post, $"/flows/api/runs/{runId}/cancel", user: "otto", roles: "flow-operator"));
        var me = await MeAsync(server.Client, Request(HttpMethod.Get, "/flows/api/me", user: "otto", roles: "flow-operator"));

        // Assert
        Assert.NotEqual(HttpStatusCode.Forbidden, cancel.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, cancel.StatusCode);
        Assert.True(me.GetProperty("canOperate").GetBoolean());
    }

    [Fact]
    public async Task Signed_in_user_without_a_dashboard_role_is_forbidden_from_viewing()
    {
        // Arrange
        using var server = Server(basic: false, identity: true);

        // Act
        var response = await server.Client.SendAsync(Request(HttpMethod.Get, "/flows/api/runs", user: "nobody", roles: "billing"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Keycloak_realm_roles_grant_operate_through_the_claims_transformation()
    {
        // Arrange — the role only exists inside Keycloak's realm_access JSON object.
        using var server = Server(basic: false, identity: true, services: s => s.AddFlowDashboardKeycloakRoles("flow-dashboard"));

        // Act
        var me = await MeAsync(server.Client, Request(HttpMethod.Get, "/flows/api/me", user: "kc", realmAccess: "{\"roles\":[\"flow-operator\",\"offline_access\"]}"));

        // Assert
        Assert.True(me.GetProperty("canOperate").GetBoolean());
    }

    [Fact]
    public async Task Configured_roles_and_policies_replace_the_defaults()
    {
        // Arrange
        using var server = Server(basic: false, identity: true,
            configure: o =>
            {
                o.Authorization.ViewerRoles = ["support"];
                o.Authorization.OperatePolicy = "OnCall";
            },
            services: s => s.AddAuthorization(a => a.AddPolicy("OnCall", p => p.RequireClaim("oncall", "true"))));

        // Act
        var supportMe = await MeAsync(server.Client, Request(HttpMethod.Get, "/flows/api/me", user: "sam", roles: "support"));
        var defaultViewer = await server.Client.SendAsync(Request(HttpMethod.Get, "/flows/api/runs", user: "vera", roles: "flow-viewer"));
        var request = Request(HttpMethod.Get, "/flows/api/me", user: "olga", roles: "flow-operator");
        request.Headers.Add("X-Test-OnCall", "true");
        var onCallMe = await MeAsync(server.Client, request);

        // Assert
        Assert.False(supportMe.GetProperty("canOperate").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, defaultViewer.StatusCode);
        Assert.True(onCallMe.GetProperty("canOperate").GetBoolean());
    }

    [Fact]
    public async Task Basic_and_identity_side_by_side_accept_either()
    {
        // Arrange
        using var server = Server(basic: true, identity: true);
        var client = server.CreateClient();

        // Act
        var basicMe = await MeAsync(client, Request(HttpMethod.Get, "/flows/api/me", basic: true));
        var basicWrite = await client.SendAsync(Request(HttpMethod.Post, $"/flows/api/runs/{Guid.NewGuid()}/cancel", basic: true));
        var oidcMe = await MeAsync(client, Request(HttpMethod.Get, "/flows/api/me", user: "vera", roles: "flow-viewer"));
        var page = await client.SendAsync(Request(HttpMethod.Get, "/flows", html: true));

        // Assert — Basic keeps full access; an anonymous browser goes to the identity provider, not a Basic dialog.
        Assert.Equal("basic", basicMe.GetProperty("method").GetString());
        Assert.Equal("admin", basicMe.GetProperty("user").GetString());
        Assert.True(basicMe.GetProperty("canOperate").GetBoolean());
        Assert.NotEqual(HttpStatusCode.Forbidden, basicWrite.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, basicWrite.StatusCode);
        Assert.Equal("oidc", oidcMe.GetProperty("method").GetString());
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Empty(page.Headers.WwwAuthenticate);
    }

    [Fact]
    public async Task Wrong_basic_credentials_on_an_api_call_still_get_the_basic_challenge()
    {
        // Arrange
        using var server = Server(basic: true, identity: true);
        var request = Request(HttpMethod.Get, "/flows/api/runs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:wrong")));

        // Act
        var response = await server.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Basic");
    }

    [Fact]
    public async Task Basic_only_keeps_the_existing_behaviour()
    {
        // Arrange
        using var server = Server(basic: true, identity: false);

        // Act
        var anonymous = await server.Client.SendAsync(Request(HttpMethod.Get, "/flows", html: true));
        var authenticated = await server.Client.SendAsync(Request(HttpMethod.Get, "/flows/api/runs", basic: true));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Contains(anonymous.Headers.WwwAuthenticate, h => h.Scheme == "Basic");
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }

    [Fact]
    public async Task Webhook_receive_endpoint_is_never_challenged()
    {
        // Arrange
        using var server = Server(basic: true, identity: true);
        server.FlowRepository.GetAllFlowsAsync().Returns(Array.Empty<FlowOrchestrator.Core.Abstractions.IFlowDefinition>());

        // Act — no Basic header, no session: only the webhook's own secret check may reject it.
        var response = await server.Client.SendAsync(Request(HttpMethod.Post, "/flows/api/webhook/unknown-slug"));

        // Assert
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Sign_out_without_identity_sign_in_returns_to_the_dashboard()
    {
        // Arrange
        using var server = new DashboardTestServer();

        // Act
        var response = await server.Client.GetAsync("/flows/signout");

        // Assert
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/flows", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public void Unregistered_scheme_fails_at_startup()
    {
        // Arrange

        // Act
        var ex = Record.Exception(() => DashboardTestServer.WithServices(s =>
        {
            s.AddAuthentication();
            s.AddFlowDashboard(o => o.Authorization.AuthenticationScheme = "Cookies");
        }));

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("'Cookies'", invalid.Message);
    }

    [Fact]
    public async Task Viewer_gets_masked_manifests_and_operator_gets_them_as_stored()
    {
        // Arrange — a viewer must not be able to copy a webhook secret and trigger through the webhook endpoint.
        using var server = Server(basic: false, identity: true);
        var flow = new FlowDefinitionRecord
        {
            Id = Guid.NewGuid(),
            Name = "Orders",
            ManifestJson = "{\"triggers\":{\"hook\":{\"type\":\"Webhook\",\"inputs\":{\"webhookSecret\":\"s3cr3t-value\"}}}}",
        };
        server.FlowStore.GetAllAsync().Returns([flow]);
        server.FlowStore.GetByIdAsync(flow.Id).Returns(flow);

        // Act
        var viewerList = await (await server.Client.SendAsync(Request(HttpMethod.Get, "/flows/api/flows", user: "vera", roles: "flow-viewer"))).Content.ReadAsStringAsync();
        var viewerOne = await (await server.Client.SendAsync(Request(HttpMethod.Get, $"/flows/api/flows/{flow.Id}", user: "vera", roles: "flow-viewer"))).Content.ReadAsStringAsync();
        var operatorOne = await (await server.Client.SendAsync(Request(HttpMethod.Get, $"/flows/api/flows/{flow.Id}", user: "otto", roles: "flow-operator"))).Content.ReadAsStringAsync();

        // Assert
        Assert.DoesNotContain("s3cr3t-value", viewerList);
        Assert.DoesNotContain("s3cr3t-value", viewerOne);
        Assert.Contains("redacted", viewerOne);
        Assert.Contains("s3cr3t-value", operatorOne);
        Assert.Equal("s3cr3t-value", System.Text.Json.Nodes.JsonNode.Parse(flow.ManifestJson)!["triggers"]!["hook"]!["inputs"]!["webhookSecret"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cross_site_state_changing_call_is_blocked_whatever_the_credentials(bool useBasic)
    {
        // Arrange — a forged form post from another site carries the cookie / cached Basic credentials.
        using var server = Server(basic: true, identity: true);
        var request = useBasic
            ? Request(HttpMethod.Post, $"/flows/api/runs/{Guid.NewGuid()}/cancel", basic: true)
            : Request(HttpMethod.Post, $"/flows/api/runs/{Guid.NewGuid()}/cancel", user: "otto", roles: "flow-operator");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        var sameOrigin = Request(HttpMethod.Post, $"/flows/api/runs/{Guid.NewGuid()}/cancel", user: "otto", roles: "flow-operator");
        sameOrigin.Headers.Add("Sec-Fetch-Site", "same-origin");
        var foreignOrigin = Request(HttpMethod.Post, $"/flows/api/runs/{Guid.NewGuid()}/cancel", basic: true);
        foreignOrigin.Headers.Add("Origin", "https://evil.example.com");

        // Act
        var blocked = await server.Client.SendAsync(request);
        var allowed = await server.Client.SendAsync(sameOrigin);
        var blockedByOrigin = await server.Client.SendAsync(foreignOrigin);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("Cross-site", await blocked.Content.ReadAsStringAsync());
        Assert.NotEqual(HttpStatusCode.Forbidden, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, blockedByOrigin.StatusCode);
    }

    [Fact]
    public async Task Signed_in_user_without_a_role_can_still_reach_me_and_signout()
    {
        // Arrange
        using var server = Server(basic: false, identity: true);

        // Act
        var me = await server.Client.SendAsync(Request(HttpMethod.Get, "/flows/api/me", user: "nobody", roles: "billing"));
        var runs = await server.Client.SendAsync(Request(HttpMethod.Get, "/flows/api/runs", user: "nobody", roles: "billing"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, runs.StatusCode);
    }

    [Fact]
    public void Unregistered_policy_fails_at_startup()
    {
        // Arrange

        // Act
        var ex = Record.Exception(() => Server(basic: false, identity: true, configure: o => o.Authorization.OperatePolicy = "Missing"));

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("'Missing'", invalid.Message);
    }

    /// <summary>
    /// Authenticates from <c>X-Test-*</c> headers; challenges with a redirect, like the OIDC handler would.
    /// </summary>
    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers["X-Test-User"].ToString();
            if (string.IsNullOrEmpty(user))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(Scheme.Name, "preferred_username", "roles");
            identity.AddClaim(new Claim("preferred_username", user));
            foreach (var role in Request.Headers["X-Test-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
                identity.AddClaim(new Claim("roles", role));
            if (Request.Headers.TryGetValue("X-Test-Realm-Access", out var realm))
                identity.AddClaim(new Claim("realm_access", realm.ToString()));
            if (Request.Headers.TryGetValue("X-Test-OnCall", out var onCall))
                identity.AddClaim(new Claim("oncall", onCall.ToString()));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.Redirect("/test-login");
            return Task.CompletedTask;
        }
    }
}
