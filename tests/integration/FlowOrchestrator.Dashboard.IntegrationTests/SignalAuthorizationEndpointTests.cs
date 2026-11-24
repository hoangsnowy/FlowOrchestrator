using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Per-signal authorization on <c>POST /flows/api/runs/{runId}/signals/{signalName}</c>: a <c>WaitForSignal</c>
/// step that declares <c>allowedRoles</c> / <c>authorizationPolicy</c> is delivered only by a signed-in identity
/// that satisfies it — never by Basic Auth, an anonymous caller or an operator without the role — while an
/// unrestricted signal keeps the operator rule. Also covers machine-to-machine access through
/// <see cref="FlowDashboardAuthorizationOptions.AdditionalAuthenticationSchemes"/>, the run-detail hint the page
/// uses, the audit log, and the startup validation.
/// </summary>
public sealed class SignalAuthorizationEndpointTests
{
    private const string CookieScheme = "Test";
    private const string BearerScheme = "TestBearer";
    private const string ValidToken = "valid-service-token";
    private static readonly string BasicHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:correct-horse-battery"));
    private static readonly Guid FlowId = new("5a5a5a5a-0000-0000-0000-000000000001");

    // ── Arrange helpers ─────────────────────────────────────────────────────────────────────────

    private static DashboardTestServer Server(
        IFlowDefinition? flow = null,
        bool basic = false,
        bool identity = true,
        bool bearer = false,
        Action<IServiceCollection>? services = null,
        string? environment = null)
    {
        var server = DashboardTestServer.WithServices(s =>
        {
            var auth = s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(CookieScheme, _ => { });
            if (bearer)
                auth.AddScheme<AuthenticationSchemeOptions, BearerAuthHandler>(BearerScheme, _ => { });
            services?.Invoke(s);
            s.AddFlowDashboard(o =>
            {
                if (basic)
                {
                    o.BasicAuth.Username = "admin";
                    o.BasicAuth.Password = "correct-horse-battery";
                }
                if (identity)
                    o.Authorization.AuthenticationScheme = CookieScheme;
                if (bearer)
                    o.Authorization.AdditionalAuthenticationSchemes = [BearerScheme];
            });
        }, environment);
        if (flow is not null)
            server.Flows = new FlowRegistry([flow]);
        return server;
    }

    private static TestFlow Flow(params (string Key, StepMetadata Step)[] steps)
    {
        var collection = new StepCollection();
        foreach (var (key, step) in steps)
            collection[key] = step;
        return new TestFlow(FlowId, collection);
    }

    private static StepMetadata WaitFor(string signalName, string[]? roles = null, string? policy = null)
    {
        var inputs = new Dictionary<string, object?> { ["signalName"] = signalName };
        if (roles is not null) inputs["allowedRoles"] = roles;
        if (policy is not null) inputs["authorizationPolicy"] = policy;
        return new StepMetadata { Type = "WaitForSignal", Inputs = inputs };
    }

    private static Guid ArrangeRunningRun(DashboardTestServer server, string signalName = "finance-approval", string stepKey = "wait_finance", IReadOnlyList<FlowStepRecord>? steps = null)
    {
        var runId = Guid.NewGuid();
        server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = FlowId, Status = "Running", Steps = steps });
        server.SignalDispatcher
            .DispatchAsync(runId, signalName, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<SignalDeliveryResult>(new SignalDeliveryResult(SignalDeliveryStatus.Delivered, stepKey, DateTimeOffset.UtcNow)));
        return runId;
    }

    private static HttpRequestMessage Signal(Guid runId, string signalName = "finance-approval", string? user = null, string? roles = null,
        bool basic = false, string? bearer = null, string? realmAccess = null, bool financeLead = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/flows/api/runs/{runId}/signals/{signalName}")
        {
            Content = new StringContent("""{"approved":true}""", Encoding.UTF8, "application/json"),
        };
        AddIdentity(request, user, roles, basic, bearer, realmAccess, financeLead);
        return request;
    }

    private static HttpRequestMessage Get(string path, string? user = null, string? roles = null, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        AddIdentity(request, user, roles, basic: false, bearer, realmAccess: null, financeLead: false);
        return request;
    }

    private static void AddIdentity(HttpRequestMessage request, string? user, string? roles, bool basic, string? bearer, string? realmAccess, bool financeLead)
    {
        if (user is not null) request.Headers.Add("X-Test-User", user);
        if (roles is not null) request.Headers.Add("X-Test-Roles", roles);
        if (realmAccess is not null) request.Headers.Add("X-Test-Realm-Access", realmAccess);
        if (financeLead) request.Headers.Add("X-Test-Finance-Lead", "true");
        if (basic) request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicHeader);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
    }

    private static Task ReceivedNoDelivery(DashboardTestServer server)
        => server.SignalDispatcher.DidNotReceiveWithAnyArgs().DispatchAsync(default, default!, default!, default).AsTask();

    // ── Restricted by role ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Restricted_signal_is_delivered_for_a_caller_with_an_allowed_role()
    {
        // Arrange
        using var server = Server(Flow(("wait_finance", WaitFor("finance-approval", roles: ["finance-approver"]))));
        var runId = ArrangeRunningRun(server);

        // Act
        using var response = await server.Client.SendAsync(Signal(runId, user: "fiona", roles: "finance-approver"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"delivered\":true", await response.Content.ReadAsStringAsync());
        await server.SignalDispatcher.Received(1).DispatchAsync(runId, "finance-approval", """{"approved":true}""", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Operator_without_the_allowed_role_gets_403_and_nothing_is_delivered()
    {
        // Arrange
        using var server = Server(Flow(("wait_finance", WaitFor("finance-approval", roles: ["finance-approver"]))));
        var runId = ArrangeRunningRun(server);

        // Act
        using var response = await server.Client.SendAsync(Signal(runId, user: "otto", roles: "flow-operator"));
        var body = await response.Content.ReadAsStringAsync();

        // Assert — the operator can read runs, so it is told who may approve.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("finance-approver", body);
        await ReceivedNoDelivery(server);
    }

    [Fact]
    public async Task Basic_auth_caller_gets_403_for_a_restricted_signal()
    {
        // Arrange
        using var server = Server(Flow(("wait_finance", WaitFor("finance-approval", roles: ["finance-approver"]))), basic: true);
        var runId = ArrangeRunningRun(server);

        // Act
        using var response = await server.Client.SendAsync(Signal(runId, basic: true));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await ReceivedNoDelivery(server);
    }

    [Fact]
    public async Task Anonymous_caller_in_development_gets_403_for_a_restricted_signal_but_still_delivers_an_unrestricted_one()
    {
        // Arrange — no authentication configured; Development grants anonymous operator access.
        using var server = new DashboardTestServer();
        server.Flows = new FlowRegistry([Flow(
            ("wait_finance", WaitFor("finance-approval", roles: ["finance-approver"])),
            ("wait_manager", WaitFor("manager-approval")))]);
        var runId = ArrangeRunningRun(server);
        server.SignalDispatcher
            .DispatchAsync(runId, "manager-approval", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<SignalDeliveryResult>(new SignalDeliveryResult(SignalDeliveryStatus.Delivered, "wait_manager", DateTimeOffset.UtcNow)));

        // Act
        using var restricted = await server.Client.SendAsync(Signal(runId));
        using var unrestricted = await server.Client.SendAsync(Signal(runId, "manager-approval"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, restricted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unrestricted.StatusCode);
        await server.SignalDispatcher.DidNotReceive().DispatchAsync(runId, "finance-approval", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Approver_without_a_dashboard_role_can_signal_but_cannot_read_runs()
    {
        // Arrange
        using var server = Server(Flow(("wait_finance", WaitFor("finance-approval", roles: ["finance-approver"]))));
        var runId = ArrangeRunningRun(server);

        // Act
        using var signal = await server.Client.SendAsync(Signal(runId, user: "fiona", roles: "finance-approver"));
        using var runs = await server.Client.SendAsync(Get("/flows/api/runs", user: "fiona", roles: "finance-approver"));
        using var detail = await server.Client.SendAsync(Get($"/flows/api/runs/{runId}", user: "fiona", roles: "finance-approver"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, signal.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, runs.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, detail.StatusCode);
    }

    [Fact]
    public async Task Keycloak_realm_role_satisfies_a_restricted_signal_through_the_claims_transformation()
    {
        // Arrange — the role only exists inside Keycloak's realm_access JSON object.
        using var server = Server(
            Flow(("wait_finance", WaitFor("finance-approval", roles: ["finance-approver"]))),
            services: s => s.AddFlowDashboardKeycloakRoles("flow-dashboard"));
        var runId = ArrangeRunningRun(server);

        // Act
        using var response = await server.Client.SendAsync(Signal(runId, user: "kc", realmAccess: "{\"roles\":[\"finance-approver\"]}"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Restricted by policy ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Policy_restricted_signal_is_delivered_only_when_the_policy_is_satisfied()
    {
        // Arrange
        using var server = Server(
            Flow(("wait_finance", WaitFor("finance-approval", policy: "FinanceLead"))),
            services: s => s.AddAuthorization(a => a.AddPolicy("FinanceLead", p => p.RequireClaim("finance_lead", "true"))));
        var runId = ArrangeRunningRun(server);

        // Act
        using var lead = await server.Client.SendAsync(Signal(runId, user: "lena", financeLead: true));
        using var operatorWithoutClaim = await server.Client.SendAsync(Signal(runId, user: "otto", roles: "flow-operator"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, lead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, operatorWithoutClaim.StatusCode);
        await server.SignalDispatcher.Received(1).DispatchAsync(runId, "finance-approval", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Roles_and_policy_together_require_both()
    {
        // Arrange
        using var server = Server(
            Flow(("wait_finance", WaitFor("finance-approval", roles: ["finance-approver"], policy: "FinanceLead"))),
            services: s => s.AddAuthorization(a => a.AddPolicy("FinanceLead", p => p.RequireClaim("finance_lead", "true"))));
        var runId = ArrangeRunningRun(server);

        // Act
        using var roleOnly = await server.Client.SendAsync(Signal(runId, user: "fiona", roles: "finance-approver"));
        using var policyOnly = await server.Client.SendAsync(Signal(runId, user: "lena", financeLead: true));
        using var both = await server.Client.SendAsync(Signal(runId, user: "fred", roles: "finance-approver", financeLead: true));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, roleOnly.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, policyOnly.StatusCode);
        Assert.Equal(HttpStatusCode.OK, both.StatusCode);
    }

    // ── Unrestricted signals keep the operator rule ─────────────────────────────────────────────

    [Fact]
    public async Task Unrestricted_signal_still_needs_the_operator_role()
    {
        // Arrange
        using var server = Server(Flow(("wait_manager", WaitFor("manager-approval"))));
        var runId = ArrangeRunningRun(server, "manager-approval", "wait_manager");

        // Act
        using var asOperator = await server.Client.SendAsync(Signal(runId, "manager-approval", user: "otto", roles: "flow-operator"));
        using var asViewer = await server.Client.SendAsync(Signal(runId, "manager-approval", user: "vera", roles: "flow-viewer"));
        using var asNobody = await server.Client.SendAsync(Signal(runId, "manager-approval", user: "nobody", roles: "billing"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, asOperator.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asViewer.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asNobody.StatusCode);
        await server.SignalDispatcher.Received(1).DispatchAsync(runId, "manager-approval", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missing_run_is_404_for_an_operator_and_403_for_anyone_else()
    {
        // Arrange
        using var server = Server(Flow(("wait_finance", WaitFor("finance-approval", roles: ["finance-approver"]))));
        var missing = Guid.NewGuid();
        server.FlowRunStore.GetRunDetailAsync(missing).Returns(default(FlowRunRecord?));

        // Act
        using var asOperator = await server.Client.SendAsync(Signal(missing, user: "otto", roles: "flow-operator"));
        using var asApprover = await server.Client.SendAsync(Signal(missing, user: "fiona", roles: "finance-approver"));
        using var asNobody = await server.Client.SendAsync(Signal(missing, user: "nobody", roles: "billing"));

        // Assert — a caller that cannot read runs cannot use the endpoint to probe which run ids exist.
        Assert.Equal(HttpStatusCode.NotFound, asOperator.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asApprover.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asNobody.StatusCode);
    }

    [Fact]
    public async Task Unauthorized_caller_is_refused_before_the_run_status_is_revealed()
    {
        // Arrange
        using var server = Server(Flow(("wait_finance", WaitFor("finance-approval", roles: ["finance-approver"]))));
        var runId = Guid.NewGuid();
        server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = FlowId, Status = "Succeeded" });

        // Act
        using var asNobody = await server.Client.SendAsync(Signal(runId, user: "nobody", roles: "billing"));
        using var asApprover = await server.Client.SendAsync(Signal(runId, user: "fiona", roles: "finance-approver"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, asNobody.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, asApprover.StatusCode);
    }

    // ── Nested steps ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Restriction_on_a_WaitForSignal_step_nested_in_a_ForEach_is_enforced()
    {
        // Arrange
        var loop = new LoopStepMetadata
        {
            Type = "ForEach",
            ForEach = "@triggerBody()?.invoices",
            Steps = new StepCollection { ["approve"] = WaitFor("invoice-approval", roles: ["finance-approver"]) },
        };
        using var server = Server(Flow(("invoices", loop)));
        var runId = ArrangeRunningRun(server, "invoice-approval", "invoices.0.approve");

        // Act
        using var asOperator = await server.Client.SendAsync(Signal(runId, "invoice-approval", user: "otto", roles: "flow-operator"));
        using var asApprover = await server.Client.SendAsync(Signal(runId, "invoice-approval", user: "fiona", roles: "finance-approver"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, asOperator.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asApprover.StatusCode);
        Assert.Contains("invoices.0.approve", await asApprover.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Two_steps_sharing_a_signal_name_require_every_restriction()
    {
        // Arrange — the store delivers to whichever waiter is oldest, so the caller must qualify for both.
        using var server = Server(Flow(
            ("wait_a", WaitFor("approval", roles: ["finance-approver"])),
            ("wait_b", WaitFor("approval", roles: ["legal-approver"]))));
        var runId = ArrangeRunningRun(server, "approval", "wait_a");

        // Act
        using var financeOnly = await server.Client.SendAsync(Signal(runId, "approval", user: "fiona", roles: "finance-approver"));
        using var both = await server.Client.SendAsync(Signal(runId, "approval", user: "bo", roles: "finance-approver,legal-approver"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, financeOnly.StatusCode);
        Assert.Equal(HttpStatusCode.OK, both.StatusCode);
    }

    // ── Machine-to-machine (bearer) ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bearer_token_through_an_additional_scheme_delivers_a_restricted_signal()
    {
        // Arrange
        using var server = Server(Flow(("wait_finance", WaitFor("finance-approval", roles: ["finance-approver"]))), bearer: true);
        var runId = ArrangeRunningRun(server);

        // Act
        using var response = await server.Client.SendAsync(Signal(runId, bearer: ValidToken));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await server.SignalDispatcher.Received(1).DispatchAsync(runId, "finance-approval", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalid_bearer_token_gets_401_not_a_redirect_on_api_and_page_requests()
    {
        // Arrange
        using var server = Server(Flow(("wait_finance", WaitFor("finance-approval", roles: ["finance-approver"]))), bearer: true);
        var runId = ArrangeRunningRun(server);
        var page = Get("/flows", bearer: "forged");
        page.Headers.Accept.ParseAdd("text/html");

        // Act
        using var signal = await server.Client.SendAsync(Signal(runId, bearer: "forged"));
        using var pageResponse = await server.Client.SendAsync(page);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, signal.StatusCode);
        Assert.Contains(signal.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        Assert.Equal(HttpStatusCode.Unauthorized, pageResponse.StatusCode);
        await ReceivedNoDelivery(server);
    }

    [Fact]
    public async Task Cookie_session_still_works_next_to_an_additional_bearer_scheme()
    {
        // Arrange
        using var server = Server(bearer: true);

        // Act
        using var cookie = await server.Client.SendAsync(Get("/flows/api/me", user: "vera", roles: "flow-viewer"));
        using var noCredentials = await server.Client.SendAsync(Get("/flows/api/runs"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, cookie.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noCredentials.StatusCode);
    }

    // ── Run detail hint for the page ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_detail_tells_the_page_who_may_deliver_a_parked_restricted_signal()
    {
        // Arrange
        using var server = Server(Flow(
            ("wait_finance", WaitFor("finance-approval", roles: ["finance-approver"])),
            ("wait_manager", WaitFor("manager-approval"))));
        var steps = new[]
        {
            new FlowStepRecord { StepKey = "wait_finance", StepType = "WaitForSignal", Status = "Pending" },
            new FlowStepRecord { StepKey = "wait_manager", StepType = "WaitForSignal", Status = "Pending" },
        };
        var runId = ArrangeRunningRun(server, steps: steps);

        // Act
        using var asViewer = await server.Client.SendAsync(Get($"/flows/api/runs/{runId}", user: "vera", roles: "flow-viewer"));
        using var asApprover = await server.Client.SendAsync(Get($"/flows/api/runs/{runId}", user: "fiona", roles: "flow-viewer,finance-approver"));
        using var viewerDoc = JsonDocument.Parse(await asViewer.Content.ReadAsStringAsync());
        using var approverDoc = JsonDocument.Parse(await asApprover.Content.ReadAsStringAsync());

        // Assert
        Assert.Equal(HttpStatusCode.OK, asViewer.StatusCode);
        var viewerHint = viewerDoc.RootElement.GetProperty("signalAuthorization");
        Assert.False(viewerHint.TryGetProperty("wait_manager", out _));
        var finance = viewerHint.GetProperty("wait_finance");
        Assert.False(finance.GetProperty("canSignal").GetBoolean());
        Assert.Equal("finance-approver", finance.GetProperty("allowedRoles")[0].GetString());
        Assert.True(approverDoc.RootElement.GetProperty("signalAuthorization").GetProperty("wait_finance").GetProperty("canSignal").GetBoolean());
        Assert.Equal(runId, approverDoc.RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Run_detail_without_a_parked_restricted_signal_keeps_its_shape()
    {
        // Arrange
        using var server = Server(Flow(("wait_manager", WaitFor("manager-approval"))));
        var steps = new[] { new FlowStepRecord { StepKey = "wait_manager", StepType = "WaitForSignal", Status = "Pending" } };
        var runId = ArrangeRunningRun(server, steps: steps);

        // Act
        using var response = await server.Client.SendAsync(Get($"/flows/api/runs/{runId}", user: "otto", roles: "flow-operator"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(doc.RootElement.TryGetProperty("signalAuthorization", out _));
    }

    // ── Audit ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delivery_and_refusal_of_a_restricted_signal_are_audited()
    {
        // Arrange
        var sink = new CapturingLoggerProvider();
        using var server = Server(
            Flow(("wait_finance", WaitFor("finance-approval", roles: ["finance-approver"]))),
            services: s => s.AddSingleton<ILoggerProvider>(sink));
        var runId = ArrangeRunningRun(server);

        // Act
        using var denied = await server.Client.SendAsync(Signal(runId, user: "otto", roles: "flow-operator"));
        using var delivered = await server.Client.SendAsync(Signal(runId, user: "fiona", roles: "finance-approver"));

        // Assert
        var refusal = Assert.Single(sink.Entries, e => e.EventId == 4108);
        Assert.Contains("otto", refusal.Message);
        Assert.Contains(runId.ToString(), refusal.Message);
        var audit = Assert.Single(sink.Entries, e => e.EventId == 4107);
        Assert.Contains("fiona", audit.Message);
        Assert.Contains("finance-approval", audit.Message);
        Assert.Contains("wait_finance", audit.Message);
    }

    // ── Startup validation ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Restricted_step_naming_an_unregistered_policy_fails_at_startup()
    {
        // Arrange
        var flows = new FlowRegistry([Flow(("wait_finance", WaitFor("finance-approval", policy: "Missing")))]);

        // Act
        var ex = Record.Exception(() => Server(services: s => s.AddSingleton(flows)));

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("'Missing'", invalid.Message);
        Assert.Contains("wait_finance", invalid.Message);
    }

    [Fact]
    public void Restricted_step_with_an_expression_signal_name_fails_at_startup()
    {
        // Arrange
        var flows = new FlowRegistry([Flow(("wait_finance", WaitFor("@triggerBody()?.signal", roles: ["finance-approver"])))]);

        // Act
        var ex = Record.Exception(() => Server(services: s => s.AddSingleton(flows)));

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("signalName", invalid.Message);
    }

    [Fact]
    public void Additional_schemes_must_be_registered()
    {
        // Arrange

        // Act
        var ex = Record.Exception(() => DashboardTestServer.WithServices(s =>
        {
            s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(CookieScheme, _ => { });
            s.AddFlowDashboard(o =>
            {
                o.Authorization.AuthenticationScheme = CookieScheme;
                o.Authorization.AdditionalAuthenticationSchemes = ["Bearer"];
            });
        }));

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("'Bearer'", invalid.Message);
    }

    [Fact]
    public void Additional_schemes_without_an_authentication_scheme_fail_at_startup()
    {
        // Arrange

        // Act
        var ex = Record.Exception(() => DashboardTestServer.WithServices(s =>
            s.AddFlowDashboard(o => o.Authorization.AdditionalAuthenticationSchemes = ["Bearer"]),
            Environments.Production));

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("AdditionalAuthenticationSchemes", invalid.Message);
    }

    // ── Test doubles ────────────────────────────────────────────────────────────────────────────

    private sealed class TestFlow(Guid id, StepCollection steps) : IFlowDefinition
    {
        public Guid Id { get; } = id;
        public string Version => "1.0";
        public FlowManifest Manifest { get; set; } = new() { Steps = steps };
    }

    /// <summary>Stands in for the cookie the OIDC handler issues: authenticates from <c>X-Test-*</c> headers.</summary>
    private sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
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
            if (Request.Headers.ContainsKey("X-Test-Finance-Lead"))
                identity.AddClaim(new Claim("finance_lead", "true"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.Redirect("/test-login");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Stands in for <c>AddJwtBearer</c>: accepts one fixed token as a Keycloak service account carrying the
    /// <c>finance-approver</c> role, fails any other bearer token, and challenges with
    /// <c>401 WWW-Authenticate: Bearer</c> as the JWT handler does.
    /// </summary>
    private sealed class BearerAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(AuthenticateResult.NoResult());
            if (header["Bearer ".Length..] != ValidToken)
                return Task.FromResult(AuthenticateResult.Fail("invalid_token"));

            var identity = new ClaimsIdentity(Scheme.Name, "preferred_username", "roles");
            identity.AddClaim(new Claim("preferred_username", "service-account-erp"));
            identity.AddClaim(new Claim("roles", "finance-approver"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.Append("WWW-Authenticate", "Bearer error=\"invalid_token\"");
            return Task.CompletedTask;
        }
    }

    private sealed record LogEntry(int EventId, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new LogEntry(eventId.Id, formatter(state, exception)));
        }
    }
}
