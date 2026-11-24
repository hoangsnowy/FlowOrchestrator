using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using FlowOrchestrator.Core.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Every read endpoint that serves stored payloads, called by the three kinds of caller that matter: a
/// signed-in <c>flow-viewer</c>, an anonymous caller on an unprotected dashboard outside Development (read-only),
/// and a <c>flow-operator</c>. The first two must never receive a credential value — whatever spelling its name
/// uses (<c>X-Api-Key</c>, <c>private_key</c>, <c>Ocp-Apim-Subscription-Key</c>, …) — and the operator must
/// receive every value as stored.
/// </summary>
/// <remarks>Each test owns its server (<c>using var</c>) so NSubstitute arrangements never leak across tests.</remarks>
public sealed class DashboardViewerSecretMaskingTests
{
    /// <summary>Who is calling the dashboard.</summary>
    public enum Caller
    {
        /// <summary>Signed in with the <c>flow-viewer</c> role only.</summary>
        Viewer,

        /// <summary>No credentials, unprotected dashboard, Production environment: read-only.</summary>
        AnonymousReadOnly,

        /// <summary>Signed in with the <c>flow-operator</c> role.</summary>
        Operator,
    }

    private const string Scheme = "Test";
    private const string OrdinaryValue = "visible-order-4711";

    // Credential names as real integrations spell them, each with a value unique enough to search for.
    private static readonly (string Name, string Value)[] Credentials =
    [
        ("X-Api-Key", "val-01-x-api-key"),
        ("api-key", "val-02-api-key"),
        ("Ocp-Apim-Subscription-Key", "val-03-apim"),
        ("x-functions-key", "val-04-functions"),
        ("private_key", "val-05-private-key"),
        ("hmac_key", "val-06-hmac"),
        ("pwd", "val-07-pwd"),
        ("aws_access_key_id", "val-08-aws"),
        ("client.secret", "val-09-client-secret"),
    ];

    private static string CredentialsJson()
    {
        var builder = new StringBuilder("{\"orderId\":\"").Append(OrdinaryValue).Append('"');
        foreach (var (name, value) in Credentials)
            builder.Append(",\"").Append(name).Append("\":\"").Append(value).Append('"');
        return builder.Append('}').ToString();
    }

    private static Dictionary<string, string> CredentialHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Request-Id"] = OrdinaryValue };
        foreach (var (name, value) in Credentials)
            headers[name] = value;
        return headers;
    }

    private static DashboardTestServer ServerFor(Caller caller)
        => caller == Caller.AnonymousReadOnly
            ? DashboardTestServer.InEnvironment(Environments.Production)
            : DashboardTestServer.WithServices(s =>
            {
                s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, RoleHeaderAuthHandler>(Scheme, _ => { });
                s.AddFlowDashboard(o => o.Authorization.AuthenticationScheme = Scheme);
            });

    private static async Task<string> GetAsync(DashboardTestServer server, Caller caller, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (caller != Caller.AnonymousReadOnly)
        {
            request.Headers.Add("X-Test-User", caller == Caller.Operator ? "otto" : "vera");
            request.Headers.Add("X-Test-Roles", caller == Caller.Operator ? "flow-operator" : "flow-viewer");
        }
        using var response = await server.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} answered {(int)response.StatusCode}: {body}");
        return body;
    }

    private static void AssertMaskedUnlessOperator(Caller caller, string body, params string[] extraSecretValues)
    {
        foreach (var value in Credentials.Select(c => c.Value).Concat(extraSecretValues))
        {
            if (caller == Caller.Operator)
                Assert.Contains(value, body, StringComparison.Ordinal);
            else
                Assert.DoesNotContain(value, body, StringComparison.Ordinal);
        }
        Assert.Contains(OrdinaryValue, body, StringComparison.Ordinal);
    }

    private static FlowRunRecord RunWithCredentials(Guid runId, string traceSecret) => new()
    {
        Id = runId,
        FlowId = Guid.NewGuid(),
        FlowName = "Orders",
        Status = "Succeeded",
        TriggerKey = "webhook",
        TriggerDataJson = CredentialsJson(),
        TriggerHeaders = CredentialHeaders(),
        StartedAt = DateTimeOffset.UtcNow,
        Steps =
        [
            new FlowStepRecord
            {
                RunId = runId,
                StepKey = "call",
                StepType = "Http",
                Status = "Succeeded",
                InputJson = CredentialsJson(),
                OutputJson = CredentialsJson(),
                Attempts =
                [
                    new FlowStepAttemptRecord { RunId = runId, StepKey = "call", Attempt = 1, StepType = "Http", Status = "Succeeded", InputJson = CredentialsJson(), OutputJson = CredentialsJson() },
                ],
            },
            new FlowStepRecord
            {
                RunId = runId,
                StepKey = "gate",
                StepType = "Noop",
                Status = "Skipped",
                EvaluationTraceJson = "{\"expression\":\"@triggerHeaders()['X-Api-Key'] == 'expected'\",\"resolved\":\"'" + traceSecret + "' == 'expected'\",\"result\":false}",
            },
        ],
    };

    private static FlowDefinitionRecord FlowWithCredentials(DashboardTestServer server)
    {
        var flowId = Guid.NewGuid();
        var record = new FlowDefinitionRecord
        {
            Id = flowId,
            Name = "Orders",
            ManifestJson = "{\"triggers\":{\"hook\":{\"type\":\"Webhook\",\"inputs\":" + CredentialsJson() + "}},"
                + "\"steps\":{\"call\":{\"type\":\"Http\",\"inputs\":" + CredentialsJson() + "}}}",
        };
        server.FlowStore.GetAllAsync().Returns([record]);
        server.FlowStore.GetByIdAsync(flowId).Returns(record);
        return record;
    }

    [Theory]
    [InlineData(Caller.Viewer)]
    [InlineData(Caller.AnonymousReadOnly)]
    [InlineData(Caller.Operator)]
    public async Task Flow_catalog_manifests_mask_credentials_for_read_only_callers(Caller caller)
    {
        // Arrange
        using var server = ServerFor(caller);
        var flow = FlowWithCredentials(server);

        // Act
        var list = await GetAsync(server, caller, "/flows/api/flows");
        var one = await GetAsync(server, caller, $"/flows/api/flows/{flow.Id}");

        // Assert
        AssertMaskedUnlessOperator(caller, list);
        AssertMaskedUnlessOperator(caller, one);
    }

    [Theory]
    [InlineData(Caller.Viewer)]
    [InlineData(Caller.AnonymousReadOnly)]
    [InlineData(Caller.Operator)]
    public async Task Run_detail_masks_trigger_body_headers_step_payloads_and_when_trace(Caller caller)
    {
        // Arrange — the Raw JSON tab renders this same response, so it is covered here too.
        using var server = ServerFor(caller);
        var runId = Guid.NewGuid();
        var run = RunWithCredentials(runId, traceSecret: "val-10-trace");
        server.FlowRunStore.GetRunDetailAsync(runId).Returns(_ => RunWithCredentials(runId, traceSecret: "val-10-trace"));
        server.OutputsRepository.GetTriggerHeadersAsync(runId)
            .Returns(new ValueTask<IReadOnlyDictionary<string, string>?>(run.TriggerHeaders));

        // Act
        var body = await GetAsync(server, caller, $"/flows/api/runs/{runId}");

        // Assert
        AssertMaskedUnlessOperator(caller, body, "val-10-trace");
    }

    [Theory]
    [InlineData(Caller.Viewer)]
    [InlineData(Caller.AnonymousReadOnly)]
    [InlineData(Caller.Operator)]
    public async Task Run_steps_mask_step_and_attempt_payloads(Caller caller)
    {
        // Arrange
        using var server = ServerFor(caller);
        var runId = Guid.NewGuid();
        server.FlowRunStore.GetRunDetailAsync(runId).Returns(_ => RunWithCredentials(runId, traceSecret: "val-11-trace"));

        // Act
        var body = await GetAsync(server, caller, $"/flows/api/runs/{runId}/steps");

        // Assert
        AssertMaskedUnlessOperator(caller, body, "val-11-trace");
    }

    [Theory]
    [InlineData(Caller.Viewer)]
    [InlineData(Caller.AnonymousReadOnly)]
    [InlineData(Caller.Operator)]
    public async Task Run_list_active_runs_and_run_search_mask_trigger_headers(Caller caller)
    {
        // Arrange — viewers get list rows without the trigger body, so the headers are what must be masked.
        using var server = ServerFor(caller);
        var runId = Guid.NewGuid();
        var row = RunWithCredentials(runId, traceSecret: "unused");
        row.Steps = null;
        IReadOnlyList<FlowRunRecord> rows = [row];
        server.FlowRunStore.GetRunsAsync(Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<int>()).Returns(rows);
        server.FlowRunStore.GetActiveRunsAsync().Returns(rows);
        server.FlowRunStore.SearchRunsAsync(Arg.Any<RunSearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new RunSearchPage(rows, HasMore: false, TotalCount: 1));

        // Act
        var list = await GetAsync(server, caller, "/flows/api/runs?take=10");
        var active = await GetAsync(server, caller, "/flows/api/runs/active");
        var search = await GetAsync(server, caller, "/flows/api/runs?status=Succeeded&take=8&includeTotal=true");

        // Assert
        AssertMaskedUnlessOperator(caller, list);
        AssertMaskedUnlessOperator(caller, active);
        AssertMaskedUnlessOperator(caller, search);
    }

    [Theory]
    [InlineData(Caller.Viewer)]
    [InlineData(Caller.AnonymousReadOnly)]
    [InlineData(Caller.Operator)]
    public async Task Webhook_delivery_log_masks_credential_headers(Caller caller)
    {
        // Arrange
        using var server = ServerFor(caller);
        var log = server.Services.GetRequiredService<IWebhookRejectionStore>();
        await log.WriteAsync(new WebhookRejectionRecord
        {
            FlowId = Guid.NewGuid(),
            TriggerKey = "webhook",
            ReceivedAt = DateTimeOffset.UtcNow,
            Reason = "DigestMismatch",
            StatusCode = 401,
            HeadersJson = System.Text.Json.JsonSerializer.Serialize(CredentialHeaders()),
            BodyTruncated = CredentialsJson(),
        });

        // Act
        var bare = await GetAsync(server, caller, "/flows/api/webhooks/recent");
        var paged = await GetAsync(server, caller, "/flows/api/webhooks/recent?includeTotal=true&take=50");

        // Assert
        AssertMaskedUnlessOperator(caller, bare);
        AssertMaskedUnlessOperator(caller, paged);
    }

    /// <summary>Authenticates from <c>X-Test-User</c> / <c>X-Test-Roles</c>, standing in for an OIDC cookie.</summary>
    private sealed class RoleHeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
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
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
