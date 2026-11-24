using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Diagnostics;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Dashboard.Webhooks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Audit (4th sweep) — dashboard REST + webhook defects proven through real HTTP requests against
/// <see cref="DashboardTestServer"/>. Each test failed on the audited commit; the summary of each states
/// the production impact and the expected behaviour.
/// </summary>
/// <remarks>
/// Each test owns its server (<c>using var</c>) to avoid NSubstitute last-call leakage across
/// parallel test classes.
/// </remarks>
public sealed class DashboardSurfaceAuditTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static IFlowDefinition WebhookFlow(Guid id, Dictionary<string, object?> inputs, string triggerKey = "webhook")
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(id);
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                [triggerKey] = new TriggerMetadata { Type = TriggerType.Webhook, Inputs = inputs }
            },
            Steps = new StepCollection { ["s"] = new StepMetadata { Type = "Noop" } }
        });
        return flow;
    }

    private static void RegisterEnabled(DashboardTestServer server, IFlowDefinition flow)
    {
        // Read the substitute's Id BEFORE arranging another substitute: NSubstitute would otherwise
        // attach the Returns() below to flow.Id.
        var id = flow.Id;
        server.Flows = new FlowRegistry(new[] { flow });
        server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });
    }

    private static HttpRequestMessage JsonPost(string url, byte[] body)
        => new(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(body) { Headers = { { "Content-Type", "application/json" } } },
        };

    private static string GitHubSignature(string key, byte[] body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), body)).ToLowerInvariant();

    // ── Webhook signature dialects ───────────────────────────────────────────────────────────

    /// <summary>
    /// The MicrosoftTeams spec reads its signature from the <c>Authorization</c> header, which the endpoint
    /// strips from the captured headers. The verifier must still see it, or no Teams delivery can ever be
    /// accepted in Enforce mode (the 2.0 default). Signed the way Microsoft documents it: HMAC-SHA256 keyed
    /// with the base64-decoded security token.
    /// </summary>
    [Fact]
    public async Task MicrosoftTeams_scheme_accepts_correctly_signed_delivery()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var id = Guid.NewGuid();
        var securityToken = Convert.ToBase64String(Encoding.UTF8.GetBytes("teams-signing-key-bytes"));
        RegisterEnabled(server, WebhookFlow(id, new Dictionary<string, object?>
        {
            ["webhookSlug"] = "teams",
            ["webhookHmacKey"] = securityToken,
            ["webhookSignatureScheme"] = "MicrosoftTeams",
        }));
        var body = Encoding.UTF8.GetBytes("{\"type\":\"message\",\"text\":\"hi\"}");
        using var request = JsonPost("/flows/api/webhook/teams", body);
        request.Headers.TryAddWithoutValidation("Authorization",
            "HMAC " + Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(securityToken), body)));

        // Act
        using var response = await server.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The Authorization header handed to the verifier must not leak into the trigger headers the run
    /// stores (and run detail serves).
    /// </summary>
    [Fact]
    public async Task MicrosoftTeams_delivery_does_not_capture_the_Authorization_header_as_a_trigger_header()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var id = Guid.NewGuid();
        var securityToken = Convert.ToBase64String(Encoding.UTF8.GetBytes("teams-signing-key-bytes"));
        RegisterEnabled(server, WebhookFlow(id, new Dictionary<string, object?>
        {
            ["webhookSlug"] = "teams",
            ["webhookHmacKey"] = securityToken,
            ["webhookSignatureScheme"] = "MicrosoftTeams",
        }));
        ITriggerContext? captured = null;
        server.FlowOrchestrator.TriggerAsync(Arg.Do<ITriggerContext>(c => captured = c), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<object?>((object?)null));
        var body = Encoding.UTF8.GetBytes("{\"type\":\"message\"}");
        using var request = JsonPost("/flows/api/webhook/teams", body);
        request.Headers.TryAddWithoutValidation("Authorization",
            "HMAC " + Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(securityToken), body)));

        // Act
        using var response = await server.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(captured);
        Assert.False(captured!.Trigger.Headers!.ContainsKey("Authorization"));
    }

    /// <summary>
    /// Docs: "Existing v1.24 manifests using only <c>webhookSecret</c> continue to work unchanged" and
    /// <c>webhookSecret</c> is the legacy alias of <c>webhookHmacKey</c>. Once the pipeline verified the
    /// GitHub HMAC with it, the endpoint must not also demand <c>X-Webhook-Key</c> = the HMAC key — GitHub
    /// never sends that header.
    /// </summary>
    [Fact]
    public async Task Legacy_webhookSecret_as_hmac_key_accepts_valid_GitHub_signature_without_X_Webhook_Key()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var id = Guid.NewGuid();
        const string key = "v124-github-secret";
        RegisterEnabled(server, WebhookFlow(id, new Dictionary<string, object?>
        {
            ["webhookSlug"] = "gh-legacy",
            ["webhookSecret"] = key,
            ["webhookSignatureScheme"] = "GitHub",
        }));
        var body = Encoding.UTF8.GetBytes("{\"action\":\"opened\"}");
        using var request = JsonPost("/flows/api/webhook/gh-legacy", body);
        request.Headers.Add("X-Hub-Signature-256", GitHubSignature(key, body));

        // Act
        using var response = await server.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Docs (webhook-hardening.md, Twilio): "Twilio sends application/x-www-form-urlencoded, not JSON". The
    /// endpoint must accept the form body and hand its fields to the verifier, which signs
    /// <c>{url}{sorted key+value pairs}</c>.
    /// </summary>
    [Fact]
    public async Task Twilio_form_encoded_delivery_with_valid_signature_is_accepted()
    {
        // Arrange
        using var server = new DashboardTestServer(o => o.UseWebhookSecurity(s => s.AllowLegacySha1()));
        var id = Guid.NewGuid();
        const string authToken = "twilio-auth-token";
        RegisterEnabled(server, WebhookFlow(id, new Dictionary<string, object?>
        {
            ["webhookSlug"] = "twilio-sms",
            ["webhookHmacKey"] = authToken,
            ["webhookSignatureScheme"] = "Twilio",
        }));
        var form = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["Body"] = "hello", ["From"] = "+15550001111" };
        var url = "http://localhost/flows/api/webhook/twilio-sms";
        var signed = new StringBuilder(url);
        foreach (var (k, v) in form) signed.Append(k).Append(v);
        var signature = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(authToken), Encoding.UTF8.GetBytes(signed.ToString())));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/flows/api/webhook/twilio-sms")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Add("X-Twilio-Signature", signature);

        // Act
        using var response = await server.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A form-encoded body reaches the run as a JSON object: one string per field, an array for a field
    /// that repeats, with <c>+</c> and percent-escapes decoded.
    /// </summary>
    [Fact]
    public async Task Form_encoded_delivery_becomes_a_json_object_trigger_body()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var id = Guid.NewGuid();
        RegisterEnabled(server, WebhookFlow(id, new Dictionary<string, object?> { ["webhookSlug"] = "form" }));
        ITriggerContext? captured = null;
        server.FlowOrchestrator.TriggerAsync(Arg.Do<ITriggerContext>(c => captured = c), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<object?>((object?)null));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/flows/api/webhook/form")
        {
            Content = new StringContent("Body=hello+world&To=%2B1555&Media=a&Media=b", Encoding.UTF8, "application/x-www-form-urlencoded"),
        };

        // Act
        using var response = await server.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = Assert.IsType<System.Text.Json.JsonElement>(captured!.Trigger.Data);
        Assert.Equal("hello world", body.GetProperty("Body").GetString());
        Assert.Equal("+1555", body.GetProperty("To").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Array, body.GetProperty("Media").ValueKind);
        Assert.Equal(["a", "b"], body.GetProperty("Media").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    /// <summary>
    /// With host-wide replay protection (<c>UseReplayProtection(300)</c>, as shown in triggers.md) a
    /// body-only scheme such as GitHub — which sends no timestamp header — must be accepted: the gate keys
    /// it on the signed body hash, as <c>ReplayProtectionGate</c>'s own remarks describe, instead of
    /// demanding an (unsigned, attacker-controlled) timestamp header.
    /// </summary>
    [Fact]
    public async Task Host_wide_replay_protection_accepts_GitHub_delivery_that_carries_no_timestamp_header()
    {
        // Arrange
        using var server = new DashboardTestServer(o => o.UseWebhookSecurity(s => s.UseReplayProtection(toleranceSeconds: 300)));
        var id = Guid.NewGuid();
        const string key = "ghs_cookbook_secret";
        // Inputs exactly as the docs' GitHub cookbook entry (minus the IP preset).
        RegisterEnabled(server, WebhookFlow(id, new Dictionary<string, object?>
        {
            ["webhookSlug"] = "github-events",
            ["webhookHmacKey"] = key,
            ["webhookSignatureScheme"] = "GitHub",
            ["webhookNonceHeader"] = "X-GitHub-Delivery",
        }));
        var body = Encoding.UTF8.GetBytes("{\"zen\":\"Keep it logically awesome.\"}");
        using var request = JsonPost("/flows/api/webhook/github-events", body);
        request.Headers.Add("X-Hub-Signature-256", GitHubSignature(key, body));
        request.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString());

        // Act
        using var response = await server.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// When the signature did NOT verify (Audit mode lets the delivery through the pipeline), the legacy
    /// shared-secret check still applies — skipping it is reserved for a verified signature.
    /// </summary>
    [Fact]
    public async Task Legacy_webhookSecret_check_still_applies_when_the_signature_failed_in_audit_mode()
    {
        // Arrange
        using var server = new DashboardTestServer(o => o.UseWebhookSecurity(s => s.UseEnforcementMode(WebhookEnforcementMode.Audit)));
        var id = Guid.NewGuid();
        RegisterEnabled(server, WebhookFlow(id, new Dictionary<string, object?>
        {
            ["webhookSlug"] = "gh-audit",
            ["webhookSecret"] = "v124-github-secret",
            ["webhookSignatureScheme"] = "GitHub",
        }));
        var body = Encoding.UTF8.GetBytes("{\"action\":\"opened\"}");
        using var request = JsonPost("/flows/api/webhook/gh-audit", body);
        request.Headers.Add("X-Hub-Signature-256", GitHubSignature("some-other-key", body));

        // Act
        using var response = await server.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Client disconnect on the webhook endpoint ────────────────────────────────────────────

    /// <summary>
    /// TELEMETRY NOISE. The webhook endpoint is mapped on the root builder, not on the dashboard group,
    /// so the group's "request aborted → 499, swallow" filter did not wrap it. A client that disconnects
    /// while the body is being buffered makes <c>WebhookRequestBuffer.ReadAsync</c> throw
    /// <see cref="OperationCanceledException"/>, which escapes the endpoint as an unhandled exception
    /// (counted by <c>aspnetcore.diagnostics.exceptions</c>) — the stress-harness observation.
    /// </summary>
    [Fact]
    public async Task Webhook_client_disconnect_during_body_read_does_not_escape_as_unhandled_exception()
    {
        // Arrange
        var probe = new EscapeProbe(onlyPathContaining: "/api/webhook/", failure: AbortingBodyStream.Failure.ClientAbort);
        // Production: no DeveloperExceptionPage between the probe and the endpoint, so whatever
        // escapes the endpoint reaches the probe exactly as it reaches Kestrel in a real deployment.
        using var server = DashboardTestServer.WithServices(s =>
        {
            s.AddFlowDashboard(o => o.AllowAnonymousOperators = true);
            s.AddSingleton<IStartupFilter>(probe);
        }, Environments.Production);
        var id = Guid.NewGuid();
        RegisterEnabled(server, WebhookFlow(id, new Dictionary<string, object?> { ["webhookSlug"] = "drop" }));
        using var request = JsonPost("/flows/api/webhook/drop", "{\"a\":1}"u8.ToArray());

        // Act
        using var response = await server.Client.SendAsync(request);
        var escaped = await probe.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert — handled like every dashboard endpoint: no exception, status 499.
        Assert.Null(escaped);
        Assert.Equal(499, (int)response.StatusCode);
    }

    /// <summary>
    /// TELEMETRY NOISE. Kestrel throws <see cref="BadHttpRequestException"/> ("Unexpected end of request
    /// content") from the body stream when a client disconnects mid-upload. Neither the webhook endpoint
    /// nor the dashboard control-plane endpoints caught it (the group filter only swallowed
    /// <see cref="OperationCanceledException"/>), so it surfaced as an unhandled application exception.
    /// </summary>
    [Theory]
    [InlineData("/flows/api/webhook/drop")]
    [InlineData("/flows/api/flows/{id}/trigger")]
    public async Task Truncated_request_body_does_not_escape_as_unhandled_exception(string pathTemplate)
    {
        // Arrange
        var probe = new EscapeProbe(onlyPathContaining: "/api/", failure: AbortingBodyStream.Failure.TruncatedBody);
        // Production: no DeveloperExceptionPage between the probe and the endpoint, so whatever
        // escapes the endpoint reaches the probe exactly as it reaches Kestrel in a real deployment.
        using var server = DashboardTestServer.WithServices(s =>
        {
            s.AddFlowDashboard(o => o.AllowAnonymousOperators = true);
            s.AddSingleton<IStartupFilter>(probe);
        }, Environments.Production);
        var id = Guid.NewGuid();
        RegisterEnabled(server, WebhookFlow(id, new Dictionary<string, object?> { ["webhookSlug"] = "drop" }));
        using var request = JsonPost(pathTemplate.Replace("{id}", id.ToString()), "{\"a\":1}"u8.ToArray());

        // Act
        using var response = await server.Client.SendAsync(request);
        var escaped = await probe.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert — a handled 400 (the caller is still connected).
        Assert.Null(escaped);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Rerun ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// WRONG RESULT. Rerun rehydrated headers from <see cref="FlowRunRecord.TriggerHeaders"/>, but no
    /// store ever populates that property from <c>GetRunAsync</c> (headers live in
    /// <see cref="IOutputsRepository"/>, which the run-detail endpoint reads). A rerun therefore started
    /// with NO trigger headers: every <c>@triggerHeaders()['X-Tenant']</c> input resolved null and the
    /// rerun silently diverged from the run it claims to replay.
    /// </summary>
    [Fact]
    public async Task Rerun_replays_the_original_trigger_headers()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = WebhookFlow(flowId, new Dictionary<string, object?> { ["webhookSlug"] = "orders" });
        server.Flows = new FlowRegistry(new[] { flow });
        // Shape of the record every real store returns: TriggerHeaders is never set by GetRunAsync.
        server.FlowRunStore.GetRunDetailAsync(runId).Returns(new FlowRunRecord
        {
            Id = runId, FlowId = flowId, Status = "Succeeded", TriggerKey = "webhook", TriggerDataJson = "{\"orderId\":7}",
        });
        server.OutputsRepository.GetTriggerHeadersAsync(runId).Returns(new ValueTask<IReadOnlyDictionary<string, string>?>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Tenant"] = "acme" }));
        ITriggerContext? captured = null;
        server.FlowOrchestrator.TriggerAsync(Arg.Do<ITriggerContext>(c => captured = c), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<object?>((object?)null));

        // Act
        using var response = await server.Client.PostAsync($"/flows/api/runs/{runId}/rerun", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(captured);
        Assert.NotNull(captured!.Trigger.Headers);
        Assert.Equal("acme", captured.Trigger.Headers!["X-Tenant"]);
    }

    /// <summary>
    /// The rehydrated headers still drop the idempotency header, or the rerun would dedupe onto the
    /// original run and start nothing.
    /// </summary>
    [Fact]
    public async Task Rerun_strips_the_idempotency_header_from_the_stored_trigger_headers()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        server.Flows = new FlowRegistry(new[] { WebhookFlow(flowId, new Dictionary<string, object?> { ["webhookSlug"] = "orders" }) });
        server.FlowRunStore.GetRunDetailAsync(runId).Returns(new FlowRunRecord
        {
            Id = runId, FlowId = flowId, Status = "Succeeded", TriggerKey = "webhook",
        });
        var idempotencyHeader = new FlowOrchestrator.Core.Configuration.FlowRunControlOptions().IdempotencyHeaderName;
        server.OutputsRepository.GetTriggerHeadersAsync(runId).Returns(new ValueTask<IReadOnlyDictionary<string, string>?>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Tenant"] = "acme", [idempotencyHeader] = "order-7" }));
        ITriggerContext? captured = null;
        server.FlowOrchestrator.TriggerAsync(Arg.Do<ITriggerContext>(c => captured = c), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<object?>((object?)null));

        // Act
        using var response = await server.Client.PostAsync($"/flows/api/runs/{runId}/rerun", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("acme", captured!.Trigger.Headers!["X-Tenant"]);
        Assert.False(captured.Trigger.Headers.ContainsKey(idempotencyHeader));
    }

    // ── Cancel ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 500 ON MALFORMED INPUT. <c>POST /api/runs/{id}/cancel</c> deserialises the optional reason body
    /// without catching <c>JsonException</c>; a malformed body is a 500 instead of a 400.
    /// </summary>
    [Fact]
    public async Task Cancel_with_malformed_reason_body_returns_400()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var runId = Guid.NewGuid();
        server.FlowRunStore.GetRunDetailAsync(runId).Returns(new FlowRunRecord { Id = runId, FlowId = Guid.NewGuid(), Status = "Running" });
        using var content = new StringContent("{reason: oops", Encoding.UTF8, "application/json");

        // Act
        HttpStatusCode status;
        try
        {
            using var response = await server.Client.PostAsync($"/flows/api/runs/{runId}/cancel", content);
            status = response.StatusCode;
        }
        catch (Exception)
        {
            status = HttpStatusCode.InternalServerError;
        }

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    // ── Query parsing / compression ──────────────────────────────────────────────────────────

    /// <summary>
    /// 500 ON A VIEWER GET. A free-text run search with only <c>to</c> computes the default window as
    /// <c>to - RunSearchWindow</c>; for <c>to</c> near <see cref="DateTimeOffset.MinValue"/> the subtraction
    /// threw <see cref="ArgumentOutOfRangeException"/> out of <c>RunListRequest.Parse</c>.
    /// </summary>
    [Fact]
    public async Task Run_search_with_minimal_to_bound_does_not_500()
    {
        // Arrange
        using var server = new DashboardTestServer();

        // Act
        HttpStatusCode status;
        try
        {
            using var response = await server.Client.GetAsync("/flows/api/runs?search=order&to=0001-01-02T00:00:00Z&includeTotal=true");
            status = response.StatusCode;
        }
        catch (Exception)
        {
            status = HttpStatusCode.InternalServerError;
        }

        // Assert — 200 (empty page) or 400, never 500.
        Assert.NotEqual(HttpStatusCode.InternalServerError, status);
    }

    /// <summary>
    /// PERF / CLAUDE.md compression rule. <c>GET /api/flows/{id}/mermaid</c> writes its (multi-KB for real
    /// flows) text body raw: no <c>Accept-Encoding</c> negotiation and no <c>Vary: Accept-Encoding</c>,
    /// unlike every other dashboard endpoint. It must negotiate like the JSON endpoints.
    /// </summary>
    [Fact]
    public async Task Mermaid_endpoint_honours_Accept_Encoding_and_sets_Vary()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var id = Guid.NewGuid();
        var steps = new StepCollection();
        for (var i = 0; i < 40; i++)
            steps["step_" + i] = new StepMetadata { Type = "SomeStepType", RunAfter = i == 0 ? new RunAfterCollection() : new RunAfterCollection { ["step_" + (i - 1)] = [StepStatus.Succeeded] } };
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(id);
        flow.Manifest.Returns(new FlowManifest { Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } }, Steps = steps });
        server.Flows = new FlowRegistry(new[] { flow });
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/flows/api/flows/{id}/mermaid");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br, gzip");

        // Act
        using var response = await server.Client.SendAsync(request);
        var raw = await response.Content.ReadAsByteArrayAsync();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(flow.Manifest.ToMermaid().Length > 1024, "fixture must exceed the 1 KB threshold of the compression rule");
        Assert.NotEmpty(raw);
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
        Assert.NotEmpty(response.Content.Headers.ContentEncoding);
    }

    /// <summary>
    /// CLAUDE.md compression checklist: the Brotli and Gzip variants of the Mermaid export decompress to
    /// exactly the bytes of the uncompressed variant.
    /// </summary>
    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task Mermaid_endpoint_compressed_variant_decompresses_to_the_uncompressed_bytes(string encoding)
    {
        // Arrange
        using var server = new DashboardTestServer();
        var id = Guid.NewGuid();
        var steps = new StepCollection();
        for (var i = 0; i < 40; i++)
            steps["step_" + i] = new StepMetadata { Type = "SomeStepType", RunAfter = i == 0 ? new RunAfterCollection() : new RunAfterCollection { ["step_" + (i - 1)] = [StepStatus.Succeeded] } };
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(id);
        flow.Manifest.Returns(new FlowManifest { Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } }, Steps = steps });
        server.Flows = new FlowRegistry(new[] { flow });
        using var plainRequest = new HttpRequestMessage(HttpMethod.Get, $"/flows/api/flows/{id}/mermaid");
        using var compressedRequest = new HttpRequestMessage(HttpMethod.Get, $"/flows/api/flows/{id}/mermaid");
        compressedRequest.Headers.TryAddWithoutValidation("Accept-Encoding", encoding);

        // Act
        using var plainResponse = await server.Client.SendAsync(plainRequest);
        var plain = await plainResponse.Content.ReadAsByteArrayAsync();
        using var compressedResponse = await server.Client.SendAsync(compressedRequest);
        await using var compressedBody = await compressedResponse.Content.ReadAsStreamAsync();
        await using Stream decoder = encoding == "br"
            ? new System.IO.Compression.BrotliStream(compressedBody, System.IO.Compression.CompressionMode.Decompress)
            : new System.IO.Compression.GZipStream(compressedBody, System.IO.Compression.CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        await decoder.CopyToAsync(decompressed);

        // Assert
        Assert.Empty(plainResponse.Content.Headers.ContentEncoding);
        Assert.Contains("Accept-Encoding", plainResponse.Headers.Vary);
        Assert.Equal(encoding, Assert.Single(compressedResponse.Content.Headers.ContentEncoding));
        Assert.Equal(plain, decompressed.ToArray());
    }

    // ── Test doubles ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Startup filter that wraps the whole pipeline: for matching paths it swaps the request body for a
    /// stream that fails the way Kestrel does on a client disconnect, then records whether an exception
    /// escaped the endpoint pipeline.
    /// </summary>
    private sealed class EscapeProbe(string onlyPathContaining, AbortingBodyStream.Failure failure) : IStartupFilter
    {
        /// <summary>Completed with the escaped exception, or <see langword="null"/> when none escaped.</summary>
        public TaskCompletionSource<Exception?> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc/>
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nextMiddleware) =>
            {
                if (!(ctx.Request.Path.Value ?? string.Empty).Contains(onlyPathContaining, StringComparison.Ordinal))
                {
                    await nextMiddleware(ctx);
                    return;
                }

                var aborted = new CancellationTokenSource();
                ctx.RequestAborted = aborted.Token;
                ctx.Request.Body = new AbortingBodyStream(aborted, failure);
                try
                {
                    await nextMiddleware(ctx);
                    Completed.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    Completed.TrySetResult(ex);
                    if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 599;
                }
            });
            next(app);
        };
    }

    /// <summary>Request body stream that fails its first read like a disconnected client.</summary>
    private sealed class AbortingBodyStream(CancellationTokenSource aborted, AbortingBodyStream.Failure failure) : Stream
    {
        /// <summary>The disconnect flavour to simulate.</summary>
        public enum Failure
        {
            /// <summary>RequestAborted fires and the read throws <see cref="OperationCanceledException"/>.</summary>
            ClientAbort,

            /// <summary>Kestrel's "Unexpected end of request content" <see cref="BadHttpRequestException"/>.</summary>
            TruncatedBody,
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw Fail();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(Fail());
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException<int>(Fail());
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private Exception Fail()
        {
            if (failure == Failure.TruncatedBody)
                return new BadHttpRequestException("Unexpected end of request content.", StatusCodes.Status400BadRequest);
            aborted.Cancel();
            return new OperationCanceledException(aborted.Token);
        }
    }
}
