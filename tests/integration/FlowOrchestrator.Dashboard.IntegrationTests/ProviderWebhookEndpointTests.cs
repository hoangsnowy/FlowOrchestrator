using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Dashboard.Webhooks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Sends each supported provider's webhook delivery through the real dashboard endpoint
/// (<c>POST /flows/api/webhook/{slug}</c>) in <see cref="WebhookEnforcementMode.Enforce"/> mode, built the
/// way that provider actually builds it: its real <c>Content-Type</c>, the headers it really sends and
/// nothing else (no <c>X-Webhook-Key</c>, no <c>X-Webhook-Timestamp</c>), signed with the provider's
/// documented algorithm by an implementation independent of the production verifier.
/// </summary>
/// <remarks>
/// <para>
/// Header stripping, body buffering / parsing, the legacy shared-secret check, and the replay gate all sit
/// between the wire and <c>HmacSignatureVerifier</c>; a unit test of the verifier proves none of them. Each
/// dialect gets: accepted, tampered body → 401, wrong secret → 401, missing signature → 401, and for
/// timestamped dialects stale timestamp → rejected once replay protection is on.
/// </para>
/// <para>
/// Signatures are computed at request time (derived) so timestamps can be current. With the default inputs
/// the Slack, Twilio, GitHub and Atlassian signatures equal the providers' published vectors pinned
/// verbatim in <c>ProviderGoldenVectorTests</c> (unit project). Each test owns its server (see
/// <see cref="WebhookHardeningPipelineE2ETests"/> remarks on NSubstitute thread-local leakage).
/// </para>
/// </remarks>
public sealed class ProviderWebhookEndpointTests
{
    // ── Independent signers (do not use production code) ────────────────────────────────────────

    private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);
    private static string Hex256(byte[] key, byte[] data) => Convert.ToHexString(HMACSHA256.HashData(key, data)).ToLowerInvariant();
    private static string B64256(byte[] key, byte[] data) => Convert.ToBase64String(HMACSHA256.HashData(key, data));
    private static string Hex1(byte[] key, byte[] data) => Convert.ToHexString(HMACSHA1.HashData(key, data)).ToLowerInvariant();
    private static string B641(byte[] key, byte[] data) => Convert.ToBase64String(HMACSHA1.HashData(key, data));
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Knobs a single delivery can be built with.</summary>
    /// <param name="SigningKey">Key the "provider" signs with; <see langword="null"/> = the configured one.</param>
    /// <param name="TamperBody">Send a body that differs from the signed one.</param>
    /// <param name="OmitSignature">Leave the signature out entirely.</param>
    /// <param name="Timestamp">Unix seconds the provider stamps; <see langword="null"/> = dialect default.</param>
    public sealed record DeliveryOptions(string? SigningKey = null, bool TamperBody = false, bool OmitSignature = false, long? Timestamp = null);

    /// <summary>One provider: manifest configuration plus a builder that produces its wire request.</summary>
    /// <param name="Dialect">Manifest scheme name.</param>
    /// <param name="Key">Secret as the operator pastes it from the provider console.</param>
    /// <param name="WrongKey">Another key of the same shape.</param>
    /// <param name="NeedsSha1">Provider signs with HMAC-SHA1, so the host must allow it.</param>
    /// <param name="Inputs">Extra trigger inputs (beyond slug and key).</param>
    /// <param name="Build">Builds the request for <c>/flows/api/webhook/{slug}</c> given the key to sign with.</param>
    public sealed record ProviderDelivery(
        string Dialect,
        string Key,
        string WrongKey,
        bool NeedsSha1,
        IReadOnlyDictionary<string, object?> Inputs,
        Func<string, string, DeliveryOptions, HttpRequestMessage> Build)
    {
        /// <summary>Slug the flow is registered under.</summary>
        public string Slug => Dialect.ToLowerInvariant() + "-events";

        /// <inheritdoc/>
        public override string ToString() => Dialect;
    }

    private static HttpRequestMessage Post(string url, byte[] body, string contentType, params (string Name, string? Value)[] headers)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        foreach (var (name, value) in headers)
        {
            if (value is null) continue;
            if (!req.Headers.TryAddWithoutValidation(name, value))
                req.Content.Headers.TryAddWithoutValidation(name, value);
        }
        return req;
    }

    private static byte[] Tampered(byte[] signed) => U(Encoding.UTF8.GetString(signed).Replace("1", "2", StringComparison.Ordinal) + " ");

    private static Dictionary<string, object?> Scheme(string scheme) => new() { ["webhookSignatureScheme"] = scheme };

    /// <summary>All provider deliveries, keyed by dialect.</summary>
    public static readonly IReadOnlyDictionary<string, ProviderDelivery> Providers = BuildProviders().ToDictionary(p => p.Dialect, StringComparer.Ordinal);

    /// <summary>Every dialect name. Theory data source.</summary>
    public static TheoryData<string> AllDialects => new(Providers.Keys);

    /// <summary>Dialects whose signed material includes a timestamp.</summary>
    public static TheoryData<string> TimestampedDialects => new("Stripe", "Slack", "SlackEvents", "Zoom", "Calendly", "Mailgun");

    /// <summary>Dialects that sign only the body (or URL + body): the provider sends no timestamp at all.</summary>
    public static TheoryData<string> BodyOnlyDialects => new(
        "GitHub", "GitHubLegacy", "GitHubForm", "Bitbucket", "Atlassian", "Shopify", "Twilio", "Square",
        "Linear", "Dropbox", "MicrosoftTeams", "Generic", "Custom");

    private static IEnumerable<ProviderDelivery> BuildProviders()
    {
        // GitHub — https://docs.github.com/en/webhooks/webhook-events-and-payloads#delivery-headers
        // GitHub sends BOTH X-Hub-Signature (sha1) and X-Hub-Signature-256 when a secret is set.
        static HttpRequestMessage GitHubRequest(string slug, string key, DeliveryOptions o, string contentType, byte[] body)
        {
            var k = U(key);
            var sent = o.TamperBody ? Tampered(body) : body;
            return Post($"/flows/api/webhook/{slug}", sent, contentType,
                ("User-Agent", "GitHub-Hookshot/044aadd"),
                ("X-GitHub-Delivery", Guid.NewGuid().ToString()),
                ("X-GitHub-Event", "ping"),
                ("X-GitHub-Hook-ID", "292430182"),
                ("X-GitHub-Hook-Installation-Target-ID", "79929171"),
                ("X-GitHub-Hook-Installation-Target-Type", "repository"),
                ("X-Hub-Signature", o.OmitSignature ? null : "sha1=" + Hex1(k, body)),
                ("X-Hub-Signature-256", o.OmitSignature ? null : "sha256=" + Hex256(k, body)));
        }
        var gitHubBody = U("{\"zen\":\"Design for failure.\",\"hook_id\":292430182,\"hook\":{\"type\":\"Repository\",\"active\":true}}");

        yield return new("GitHub", "It's a Secret to Everybody", "It's a Secret to Everybody!", false, Scheme("GitHub"),
            (slug, key, o) => GitHubRequest(slug, key, o, "application/json", gitHubBody));

        yield return new("GitHubLegacy", "It's a Secret to Everybody", "It's a Secret to Everybody!", true, Scheme("GitHubLegacy"),
            (slug, key, o) => GitHubRequest(slug, key, o, "application/json", gitHubBody));

        // GitHub with the webhook's "Content type: application/x-www-form-urlencoded" option:
        // the JSON is sent as payload=<urlencoded json>, and the signature covers that form body.
        yield return new("GitHubForm", "It's a Secret to Everybody", "It's a Secret to Everybody!", false, Scheme("GitHub"),
            (slug, key, o) => GitHubRequest(slug, key, o, "application/x-www-form-urlencoded",
                U("payload=" + Uri.EscapeDataString(Encoding.UTF8.GetString(gitHubBody)))));

        // Bitbucket Cloud — https://support.atlassian.com/bitbucket-cloud/docs/event-payloads/ (request headers)
        yield return new("Bitbucket", "It's a Secret to Everybody", "It's a Secret to Everybody!", false, Scheme("Bitbucket"),
            (slug, key, o) =>
            {
                var body = U("{\"push\":{\"changes\":[]},\"repository\":{\"full_name\":\"team/repo\"}}");
                return Post($"/flows/api/webhook/{slug}", o.TamperBody ? Tampered(body) : body, "application/json",
                    ("User-Agent", "Bitbucket-Webhooks/2.0"),
                    ("X-Event-Key", "repo:push"),
                    ("X-Hook-UUID", "{2c5a5e8a-1c43-4b5e-8f1e-6f1e2e1f1a11}"),
                    ("X-Request-UUID", Guid.NewGuid().ToString()),
                    ("X-Attempt-Number", "1"),
                    ("X-Hub-Signature", o.OmitSignature ? null : "sha256=" + Hex256(U(key), body)));
            });

        // Jira Cloud — https://developer.atlassian.com/cloud/jira/platform/webhooks/
        yield return new("Atlassian", "It's a Secret to Everybody", "It's a Secret to Everybody!", false, Scheme("Atlassian"),
            (slug, key, o) =>
            {
                var body = U("{\"timestamp\":1700000000000,\"webhookEvent\":\"jira:issue_created\",\"issue\":{\"id\":\"10001\",\"key\":\"FO-1\"}}");
                return Post($"/flows/api/webhook/{slug}", o.TamperBody ? Tampered(body) : body, "application/json; charset=UTF-8",
                    ("User-Agent", "Atlassian Webhook HTTP Client"),
                    ("X-Atlassian-Webhook-Identifier", "1234567890"),
                    ("X-Atlassian-Webhook-Retry", "0"),
                    ("X-Hub-Signature", o.OmitSignature ? null : "sha256=" + Hex256(U(key), body)));
            });

        // Stripe — https://docs.stripe.com/webhooks#verify-manually ; header carries t, v1 and debug entries.
        yield return new("Stripe", "whsec_5f8e2d1c0b9a8f7e6d5c4b3a2f1e0d9c", "whsec_5f8e2d1c0b9a8f7e6d5c4b3a2f1e0d9d", false, Scheme("Stripe"),
            (slug, key, o) =>
            {
                var body = U("{\n  \"id\": \"evt_test_webhook\",\n  \"object\": \"event\",\n  \"type\": \"payment_intent.succeeded\"\n}");
                var t = (o.Timestamp ?? Now).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var sig = Hex256(U(key), U(t + ".").Concat(body).ToArray());
                var suffix = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant()[^8..];
                return Post($"/flows/api/webhook/{slug}", o.TamperBody ? Tampered(body) : body, "application/json; charset=utf-8",
                    ("User-Agent", "Stripe/1.0 (+https://stripe.com/docs/webhooks)"),
                    ("Stripe-Signature", o.OmitSignature ? null
                        : $"t={t},v1={sig},secret=whsec_****************************0d9c,destination=we_1TN7mG6oEAX7HoXLZoZugElv,payload={suffix}"));
            });

        // Slack slash command — https://docs.slack.dev/authentication/verifying-requests-from-slack
        // Body + secret + timestamp are the documented example; with defaults the signature equals the doc's v0=a2114d57….
        yield return new("Slack", "8f742231b10e8888abcd99yyyzzz85a5", "8f742231b10e8888abcd99yyyzzz85a6", false, Scheme("Slack"),
            (slug, key, o) =>
            {
                var body = U("token=xyzz0WbapA4vBCDEFasx0q6G&team_id=T1DC2JH3J&team_domain=testteamnow&channel_id=G8PSS9T3V&channel_name=foobar&user_id=U2CERLKJA&user_name=roadrunner&command=%2Fwebhook-collect&text=&response_url=https%3A%2F%2Fhooks.slack.com%2Fcommands%2FT1DC2JH3J%2F397700885554%2F96rGlfmibIGlgcZRskXaIFfN&trigger_id=398738663015.47445629121.803a0bc887a14d10d2c447fce8b6703c");
                var ts = (o.Timestamp ?? 1531420618).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Post($"/flows/api/webhook/{slug}", o.TamperBody ? Tampered(body) : body, "application/x-www-form-urlencoded",
                    ("User-Agent", "Slackbot 1.0 (+https://api.slack.com/robots)"),
                    ("X-Slack-Request-Timestamp", ts),
                    ("X-Slack-Signature", o.OmitSignature ? null : "v0=" + Hex256(U(key), U("v0:" + ts + ":").Concat(body).ToArray())));
            });

        // Slack Events API (JSON) — https://docs.slack.dev/apis/events-api/ url_verification example body.
        yield return new("SlackEvents", "8f742231b10e8888abcd99yyyzzz85a5", "8f742231b10e8888abcd99yyyzzz85a6", false, Scheme("Slack"),
            (slug, key, o) =>
            {
                var body = U("{\"token\":\"Jhj5dZrVaK7ZwHHjRyZWjbDl\",\"challenge\":\"3eZbrw1aBm2rZgRNFdxV2595E9CY3gmdALWMmHkvFXO7tYXAYM8P\",\"type\":\"url_verification\"}");
                var ts = (o.Timestamp ?? Now).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Post($"/flows/api/webhook/{slug}", o.TamperBody ? Tampered(body) : body, "application/json",
                    ("User-Agent", "Slackbot 1.0 (+https://api.slack.com/robots)"),
                    ("X-Slack-Request-Timestamp", ts),
                    ("X-Slack-Signature", o.OmitSignature ? null : "v0=" + Hex256(U(key), U("v0:" + ts + ":").Concat(body).ToArray())));
            });

        // Shopify — https://shopify.dev/docs/apps/build/webhooks/subscribe/https (delivery headers + HMAC verification)
        yield return new("Shopify", "shpss_test_client_secret", "shpss_test_client_secreu", false, Scheme("Shopify"),
            (slug, key, o) =>
            {
                var body = U("{\"id\":820982911946154508,\"email\":\"jon@doe.ca\",\"total_price\":\"199.00\"}");
                return Post($"/flows/api/webhook/{slug}", o.TamperBody ? Tampered(body) : body, "application/json",
                    ("User-Agent", "Shopify-Captain-Hook"),
                    ("X-Shopify-Topic", "orders/create"),
                    ("X-Shopify-Hmac-Sha256", o.OmitSignature ? null : B64256(U(key), body)),
                    ("X-Shopify-Shop-Domain", "example.myshopify.com"),
                    ("X-Shopify-API-Version", "2025-07"),
                    ("X-Shopify-Webhook-Id", "b54557e4-bdd9-4b37-8a5f-bf7d70bcd043"),
                    ("X-Shopify-Event-Id", "98880550-7158-44d4-b7cd-2c97c8a091b5"),
                    ("X-Shopify-Triggered-At", "2025-07-01T12:00:00.000000Z"));
            });

        // Twilio voice webhook — https://www.twilio.com/docs/usage/security : form-urlencoded POST, HMAC-SHA1 over
        // {full URL incl. query}{sorted name+value pairs}, base64. Fields are the documented example.
        yield return new("Twilio", "12345", "12346", true, Scheme("Twilio"),
            (slug, key, o) =>
            {
                var url = $"https://example.com/flows/api/webhook/{slug}?foo=1&bar=2";
                var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["CallSid"] = "CA1234567890ABCDE",
                    ["Caller"] = "+14158675310",
                    ["Digits"] = "1234",
                    ["From"] = "+14158675310",
                    ["To"] = "+18005551212",
                };
                var signed = url + string.Concat(fields.Select(kv => kv.Key + kv.Value));
                var sig = B641(U(key), U(signed));
                if (o.TamperBody) fields["Digits"] = "9999";
                var body = U(string.Join("&", fields.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value))));
                return Post(url, body, "application/x-www-form-urlencoded",
                    ("User-Agent", "TwilioProxy/1.1"),
                    ("I-Twilio-Idempotency-Token", Guid.NewGuid().ToString()),
                    ("X-Twilio-Signature", o.OmitSignature ? null : sig));
            });

        // Square — https://developer.squareup.com/docs/webhooks/step3validate : base64 HMAC-SHA256 over
        // {notification URL}{raw body}.
        yield return new("Square", "sq0wss-test-signature-key", "sq0wss-test-signature-kez", false, Scheme("Square"),
            (slug, key, o) =>
            {
                var url = $"https://example.com/flows/api/webhook/{slug}";
                var body = U("{\"merchant_id\":\"MLEFBHHSJGVHD\",\"type\":\"payment.updated\",\"event_id\":\"ac3ac95b-f97d-458c-a6e6-18981597e05f\"}");
                return Post(url, o.TamperBody ? Tampered(body) : body, "application/json",
                    ("User-Agent", "Square Connect v2"),
                    ("square-version", "2025-07-16"),
                    ("square-environment", "Production"),
                    ("x-square-hmacsha256-signature", o.OmitSignature ? null : B64256(U(key), U(url).Concat(body).ToArray())));
            });

        // Zoom — https://developers.zoom.us/docs/api/webhooks/ "Verify with Zoom's header" (documented header set).
        yield return new("Zoom", "zoom_secret_token", "zoom_secret_tokem", false, Scheme("Zoom"),
            (slug, key, o) =>
            {
                var body = U("{\"event\":\"meeting.started\",\"event_ts\":1626230691572,\"payload\":{\"account_id\":\"Wk9PTV9BQ0NPVU5UX0lE\"}}");
                var ts = (o.Timestamp ?? Now).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Post($"/flows/api/webhook/{slug}", o.TamperBody ? Tampered(body) : body, "application/json; charset=utf-8",
                    ("User-Agent", "Zoom Marketplace/1.0a"),
                    ("authorization", "Wk9PTV9BVVRIT1JJWkFUSU9O"),
                    ("x-zm-request-timestamp", ts),
                    ("x-zm-signature", o.OmitSignature ? null : "v0=" + Hex256(U(key), U("v0:" + ts + ":").Concat(body).ToArray())),
                    ("x-zm-request-id", "6009d653_d487_445d_8406_42b654974899"));
            });

        // Linear — https://linear.app/developers/webhooks (documented headers; timestamp is milliseconds, unsigned).
        yield return new("Linear", "lin_wh_test_secret", "lin_wh_test_secreu", false, Scheme("Linear"),
            (slug, key, o) =>
            {
                var body = U("{\"action\":\"create\",\"type\":\"Issue\",\"webhookTimestamp\":1676056940508}");
                return Post($"/flows/api/webhook/{slug}", o.TamperBody ? Tampered(body) : body, "application/json; charset=utf-8",
                    ("User-Agent", "Linear-Webhook"),
                    ("Accept-Charset", "utf-8"),
                    ("Linear-Delivery", Guid.NewGuid().ToString()),
                    ("Linear-Event", "Issue"),
                    ("Linear-Signature", o.OmitSignature ? null : Hex256(U(key), body)),
                    ("Linear-Timestamp", (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            });

        // Dropbox — https://www.dropbox.com/developers/reference/webhooks (notification body example verbatim).
        yield return new("Dropbox", "dbx_app_secret", "dbx_app_secreu", false, Scheme("Dropbox"),
            (slug, key, o) =>
            {
                var body = U("{\"list_folder\": {\"accounts\": [\"dbid:AAH4f99T0taONIb-OurWxbNQ6ywGRopQngc\", \"dbid:AAH4f99T0taONIb-OurWxbNQ6ywGRopQngc\"]}, \"delta\": {\"users\": [12345678, 23456789]}}");
                return Post($"/flows/api/webhook/{slug}", o.TamperBody ? Tampered(body) : body, "application/json",
                    ("User-Agent", "DropboxWebhooks/1.0"),
                    ("X-Dropbox-Signature", o.OmitSignature ? null : Hex256(U(key), body)));
            });

        // Mailgun event webhook — https://documentation.mailgun.com/docs/mailgun/user-manual/webhooks/securing-webhooks
        // Signature block in the JSON body; HMAC covers {timestamp}{token}. Tamper alters the token after signing.
        yield return new("Mailgun", "mailgun-webhook-signing-key", "mailgun-webhook-signing-kez", false, Scheme("Mailgun"),
            (slug, key, o) =>
            {
                var ts = (o.Timestamp ?? Now).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(25)).ToLowerInvariant();
                var sig = Hex256(U(key), U(ts + token));
                var sentToken = o.TamperBody ? "f" + token[1..] : token;
                var sigField = o.OmitSignature ? string.Empty : $",\"signature\":\"{sig}\"";
                var body = U($"{{\"signature\":{{\"token\":\"{sentToken}\",\"timestamp\":\"{ts}\"{sigField}}},\"event-data\":{{\"event\":\"delivered\",\"id\":\"CPgfbmQMTCKtHW6uIWtuVe\"}}}}");
                return Post($"/flows/api/webhook/{slug}", body, "application/json",
                    ("User-Agent", "mailgun/treq-16.12.0"));
            });

        // Microsoft Teams outgoing webhook — https://learn.microsoft.com/en-us/microsoftteams/platform/webhooks-and-connectors/how-to/add-outgoing-webhook
        // Key = base64-DECODED security token; "Authorization: HMAC <base64 HMAC-SHA256(body)>".
        yield return new("MicrosoftTeams", "6fMbcEmK8Zdzj5A9xXk1yW3m7Hn2pQ4rT0uVwY8zB1c=", Convert.ToBase64String(new byte[32]), false, Scheme("MicrosoftTeams"),
            (slug, key, o) =>
            {
                var body = U("{\"type\":\"message\",\"id\":\"1485983408511\",\"timestamp\":\"2017-02-01T21:10:07.437Z\",\"text\":\"<at>Sample</at> hello\"}");
                return Post($"/flows/api/webhook/{slug}", o.TamperBody ? Tampered(body) : body, "application/json; charset=utf-8",
                    ("User-Agent", "Microsoft-SkypeBotApi (Microsoft-BotFramework/3.0)"),
                    ("Authorization", o.OmitSignature ? null : "HMAC " + B64256(Convert.FromBase64String(key), body)));
            });

        // Calendly — developer.calendly.com "Webhook Signatures": Calendly-Webhook-Signature t=…,v1=… over {t}.{body}.
        yield return new("Calendly", "calendly_signing_key", "calendly_signing_kez", false, Scheme("Calendly"),
            (slug, key, o) =>
            {
                var body = U("{\"event\":\"invitee.created\",\"payload\":{\"email\":\"test@example.com\"}}");
                var t = (o.Timestamp ?? Now).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Post($"/flows/api/webhook/{slug}", o.TamperBody ? Tampered(body) : body, "application/json; charset=utf-8",
                    ("User-Agent", "Calendly"),
                    ("Calendly-Webhook-Signature", o.OmitSignature ? null : $"t={t},v1=" + Hex256(U(key), U(t + ".").Concat(body).ToArray())));
            });

        // Generic — FlowOrchestrator's own dialect: X-Signature: sha256=<hex>.
        yield return new("Generic", "generic-secret", "generic-secreu", false, Scheme("Generic"),
            (slug, key, o) =>
            {
                var body = U("{\"event\":\"ping\",\"n\":1}");
                return Post($"/flows/api/webhook/{slug}", o.TamperBody ? Tampered(body) : body, "application/json",
                    ("X-Signature", o.OmitSignature ? null : "sha256=" + Hex256(U(key), body)));
            });

        // Custom — manifest-driven spec describing GitHub's modern header, fed a GitHub delivery.
        yield return new("Custom", "It's a Secret to Everybody", "It's a Secret to Everybody!", false,
            new Dictionary<string, object?>
            {
                ["webhookSignatureScheme"] = "Custom",
                ["webhookSignatureHeader"] = "X-Hub-Signature-256",
                ["webhookSignatureAlgorithm"] = "Sha256",
                ["webhookSignatureEncoding"] = "HexLower",
                ["webhookSignaturePrefix"] = "sha256=",
                ["webhookSignedPayloadStrategy"] = "RawBody",
            },
            (slug, key, o) => GitHubRequest(slug, key, o, "application/json", gitHubBody));
    }

    // ── Server plumbing ─────────────────────────────────────────────────────────────────────────

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Records any exception that escapes the endpoint pipeline (would surface as a 500).</summary>
    private sealed class ExceptionProbe
    {
        public Exception? Escaped { get; set; }

        /// <summary>Completed when the request has left the pipeline, either way.</summary>
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ExceptionProbeStartupFilter(ExceptionProbe probe) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nextMw) =>
            {
                try { await nextMw(ctx); }
                catch (Exception ex) { probe.Escaped = ex; throw; }
                finally { probe.Completed.TrySetResult(); }
            });
            next(app);
        };
    }

    private static DashboardTestServer NewServer(bool sha1 = false, int replaySeconds = 0, TimeProvider? clock = null, Action<IServiceCollection>? extra = null, string? environment = null)
        => DashboardTestServer.WithServices(services =>
        {
            if (clock is not null) services.AddSingleton(clock);
            extra?.Invoke(services);
            services.AddFlowDashboard(o => o.UseWebhookSecurity(s =>
            {
                s.UseEnforcementMode(WebhookEnforcementMode.Enforce);
                if (sha1) s.AllowLegacySha1();
                if (replaySeconds > 0) s.UseReplayProtection(toleranceSeconds: replaySeconds);
            }));
        }, environment);

    private static IFlowDefinition Register(DashboardTestServer server, string slug, IReadOnlyDictionary<string, object?> inputs)
    {
        var id = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(id);
        var all = new Dictionary<string, object?>(inputs) { ["webhookSlug"] = slug };
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["webhook"] = new TriggerMetadata { Type = TriggerType.Webhook, Inputs = all },
            },
        });
        server.Flows = new FlowRegistry(new[] { flow });
        server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, IsEnabled = true });
        return flow;
    }

    private static IFlowDefinition Register(DashboardTestServer server, ProviderDelivery p) =>
        Register(server, p.Slug, new Dictionary<string, object?>(p.Inputs) { ["webhookHmacKey"] = p.Key });

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(DashboardTestServer server, HttpRequestMessage req)
    {
        using var client = server.CreateClient();
        using var res = await client.SendAsync(req);
        return (res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    // ── Per-dialect matrix ──────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllDialects))]
    public async Task Provider_delivery_is_accepted_and_starts_a_run(string dialect)
    {
        // Arrange
        var p = Providers[dialect];
        using var server = NewServer(sha1: p.NeedsSha1);
        Register(server, p);
        var req = p.Build(p.Slug, p.Key, new DeliveryOptions());

        // Act
        var (status, body) = await SendAsync(server, req);

        // Assert
        Assert.True(status == HttpStatusCode.OK, $"{dialect}: expected 200, got {(int)status} {body}");
        await server.FlowOrchestrator.Received(1).TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public async Task Tampered_body_is_rejected_401(string dialect)
    {
        // Arrange
        var p = Providers[dialect];
        using var server = NewServer(sha1: p.NeedsSha1);
        Register(server, p);
        var req = p.Build(p.Slug, p.Key, new DeliveryOptions(TamperBody: true));

        // Act
        var (status, body) = await SendAsync(server, req);

        // Assert
        Assert.True(status == HttpStatusCode.Unauthorized, $"{dialect}: expected 401, got {(int)status} {body}");
        await server.FlowOrchestrator.DidNotReceive().TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public async Task Wrong_secret_is_rejected_401(string dialect)
    {
        // Arrange
        var p = Providers[dialect];
        using var server = NewServer(sha1: p.NeedsSha1);
        Register(server, p);
        var req = p.Build(p.Slug, p.WrongKey, new DeliveryOptions());

        // Act
        var (status, body) = await SendAsync(server, req);

        // Assert
        Assert.True(status == HttpStatusCode.Unauthorized, $"{dialect}: expected 401, got {(int)status} {body}");
        await server.FlowOrchestrator.DidNotReceive().TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public async Task Missing_signature_is_rejected_401(string dialect)
    {
        // Arrange
        var p = Providers[dialect];
        using var server = NewServer(sha1: p.NeedsSha1);
        Register(server, p);
        var req = p.Build(p.Slug, p.Key, new DeliveryOptions(OmitSignature: true));

        // Act
        var (status, body) = await SendAsync(server, req);

        // Assert
        Assert.True(status == HttpStatusCode.Unauthorized, $"{dialect}: expected 401, got {(int)status} {body}");
        await server.FlowOrchestrator.DidNotReceive().TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(TimestampedDialects))]
    public async Task Stale_signed_timestamp_is_rejected_under_replay_protection(string dialect)
    {
        // Arrange — the signature is VALID (signed over the stale timestamp); only freshness fails.
        var p = Providers[dialect];
        using var server = NewServer(sha1: p.NeedsSha1, replaySeconds: 300);
        Register(server, p);
        var req = p.Build(p.Slug, p.Key, new DeliveryOptions(Timestamp: Now - 3600));

        // Act
        var (status, body) = await SendAsync(server, req);

        // Assert
        Assert.True(status is HttpStatusCode.Conflict or HttpStatusCode.Unauthorized,
            $"{dialect}: expected stale delivery rejected (409/401), got {(int)status} {body}");
        await server.FlowOrchestrator.DidNotReceive().TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(TimestampedDialects))]
    public async Task Fresh_signed_timestamp_is_accepted_under_replay_protection(string dialect)
    {
        // Arrange — Slack's default timestamp is the 2018 doc value; pin the clock next to it so the documented
        // example itself is fresh, and stamp the others with the pinned "now".
        var p = Providers[dialect];
        var now = dialect == "Slack" ? DateTimeOffset.FromUnixTimeSeconds(1531420618 + 30) : DateTimeOffset.UtcNow;
        using var server = NewServer(sha1: p.NeedsSha1, replaySeconds: 300, clock: new FixedClock(now));
        Register(server, p);
        var req = p.Build(p.Slug, p.Key, new DeliveryOptions(Timestamp: dialect == "Slack" ? null : now.ToUnixTimeSeconds()));

        // Act
        var (status, body) = await SendAsync(server, req);

        // Assert
        Assert.True(status == HttpStatusCode.OK, $"{dialect}: expected 200, got {(int)status} {body}");
    }

    [Theory]
    [MemberData(nameof(BodyOnlyDialects))]
    public async Task Body_only_scheme_is_accepted_under_host_replay_protection_and_replay_is_409(string dialect)
    {
        // Arrange — these providers send no timestamp header (GitHub, Shopify, Teams, …). Turning on host-wide
        // replay protection must not reject every genuine delivery; an identical redelivery is the replay.
        var p = Providers[dialect];
        using var server = NewServer(sha1: p.NeedsSha1, replaySeconds: 300);
        Register(server, p);
        var first = p.Build(p.Slug, p.Key, new DeliveryOptions());
        var bytes = await first.Content!.ReadAsByteArrayAsync();
        var second = p.Build(p.Slug, p.Key, new DeliveryOptions());
        var secondBytes = await second.Content!.ReadAsByteArrayAsync();

        // Act
        var (s1, b1) = await SendAsync(server, first);
        var (s2, b2) = await SendAsync(server, second);

        // Assert
        Assert.True(s1 == HttpStatusCode.OK, $"{dialect}: first delivery expected 200, got {(int)s1} {b1}");
        if (bytes.AsSpan().SequenceEqual(secondBytes))
            Assert.True(s2 == HttpStatusCode.Conflict, $"{dialect}: identical redelivery expected 409, got {(int)s2} {b2}");
    }

    // ── Configuration / wiring edge cases ───────────────────────────────────────────────────────

    [Fact]
    public async Task GitHub_with_key_in_legacy_webhookSecret_field_is_accepted_without_X_Webhook_Key()
    {
        // Arrange — v1.24-era manifests keep the key in webhookSecret; ResolveHmacKeys falls back to it for
        // the HMAC, but the endpoint's legacy bearer check then also demands X-Webhook-Key, which GitHub never sends.
        var p = Providers["GitHub"];
        using var server = NewServer();
        Register(server, p.Slug, new Dictionary<string, object?> { ["webhookSignatureScheme"] = "GitHub", ["webhookSecret"] = p.Key });

        // Act
        var (status, body) = await SendAsync(server, p.Build(p.Slug, p.Key, new DeliveryOptions()));

        // Assert
        Assert.True(status == HttpStatusCode.OK, $"expected 200, got {(int)status} {body}");
    }

    [Fact]
    public async Task GitHub_with_both_webhookSecret_and_webhookHmacKey_like_the_sample_flow_is_accepted()
    {
        // Arrange — WebhookEnterpriseSampleFlow sets both fields to the same value.
        var p = Providers["GitHub"];
        using var server = NewServer();
        Register(server, p.Slug, new Dictionary<string, object?>
        {
            ["webhookSignatureScheme"] = "GitHub",
            ["webhookSecret"] = p.Key,
            ["webhookHmacKey"] = p.Key,
        });

        // Act
        var (status, body) = await SendAsync(server, p.Build(p.Slug, p.Key, new DeliveryOptions()));

        // Assert
        Assert.True(status == HttpStatusCode.OK, $"expected 200, got {(int)status} {body}");
    }

    [Fact]
    public async Task Sample_flow_configuration_accepts_a_genuine_GitHub_delivery()
    {
        // Arrange — the exact trigger inputs of samples/.../WebhookEnterpriseSampleFlow.cs (replay 300 s with
        // webhookTimestampHeader = X-Webhook-Timestamp, nonce X-GitHub-Delivery, rate limit, IP allow list incl.
        // loopback), host in Enforce. A real GitHub delivery carries neither X-Webhook-Key nor X-Webhook-Timestamp.
        var p = Providers["GitHub"];
        using var server = NewServer();
        Register(server, "github-sample-flow", new Dictionary<string, object?>
        {
            ["webhookSecret"] = "mySharedSecret",
            ["webhookHmacKey"] = "mySharedSecret",
            ["webhookSignatureScheme"] = "GitHub",
            ["webhookReplayToleranceSeconds"] = 300,
            ["webhookTimestampHeader"] = "X-Webhook-Timestamp",
            ["webhookNonceHeader"] = "X-GitHub-Delivery",
            ["webhookRateLimitPermitsPerSecond"] = 10,
            ["webhookRateLimitBurstSize"] = 20,
        });

        // Act
        var (status, body) = await SendAsync(server, p.Build("github-sample-flow", "mySharedSecret", new DeliveryOptions()));

        // Assert
        Assert.True(status == HttpStatusCode.OK, $"expected 200, got {(int)status} {body}");
    }

    [Fact]
    public async Task Unknown_signature_scheme_name_fails_closed()
    {
        // Arrange — a typo / unsupported scheme ("Svix") resolves to no spec; the signature gate is then skipped
        // entirely and an unsigned request starts a run. Operators believe the endpoint is protected.
        using var server = NewServer();
        Register(server, "svix-events", new Dictionary<string, object?>
        {
            ["webhookSignatureScheme"] = "Svix",
            ["webhookHmacKey"] = "whsec_" + "MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw",
        });
        var req = Post("/flows/api/webhook/svix-events", U("{\"test\": 1}"), "application/json",
            ("webhook-id", "msg_forged"), ("webhook-timestamp", Now.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("webhook-signature", "v1,AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="));

        // Act
        var (status, body) = await SendAsync(server, req);

        // Assert
        Assert.True(status != HttpStatusCode.OK, $"forged delivery to an unknown scheme was accepted ({(int)status} {body})");
        await server.FlowOrchestrator.DidNotReceive().TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StandardWebhooks_library_vector_is_accepted()
    {
        // Arrange — DOC: https://github.com/standard-webhooks/standard-webhooks/blob/main/libraries/python/tests/test_webhooks.py
        // verbatim (Svix uses the same scheme). There is no built-in dialect today; "StandardWebhooks" is the
        // name the fix is expected to use. Clock pinned next to the vector's timestamp.
        using var server = NewServer(clock: new FixedClock(DateTimeOffset.FromUnixTimeSeconds(1614265330 + 10)));
        Register(server, "stdwh-events", new Dictionary<string, object?>
        {
            ["webhookSignatureScheme"] = "StandardWebhooks",
            ["webhookHmacKey"] = "whsec_" + "MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw",
        });
        var req = Post("/flows/api/webhook/stdwh-events", U("{\"test\": 2432232314}"), "application/json",
            ("webhook-id", "msg_p5jXN8AQM9LWM0D4loKWxJek"),
            ("webhook-timestamp", "1614265330"),
            ("webhook-signature", "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE="));

        // Act
        var (status, body) = await SendAsync(server, req);

        // Assert
        Assert.True(status == HttpStatusCode.OK, $"expected 200, got {(int)status} {body}");
    }

    [Fact]
    public async Task StandardWebhooks_tampered_vector_is_rejected()
    {
        // Arrange — the library's own "invalid signature" case: last base64 char changed (…1OE= → …1OA=).
        using var server = NewServer(clock: new FixedClock(DateTimeOffset.FromUnixTimeSeconds(1614265330 + 10)));
        Register(server, "stdwh-events", new Dictionary<string, object?>
        {
            ["webhookSignatureScheme"] = "StandardWebhooks",
            ["webhookHmacKey"] = "whsec_" + "MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw",
        });
        var req = Post("/flows/api/webhook/stdwh-events", U("{\"test\": 2432232314}"), "application/json",
            ("webhook-id", "msg_p5jXN8AQM9LWM0D4loKWxJek"),
            ("webhook-timestamp", "1614265330"),
            ("webhook-signature", "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OA="));

        // Act
        var (status, body) = await SendAsync(server, req);

        // Assert
        Assert.True(status == HttpStatusCode.Unauthorized, $"expected 401, got {(int)status} {body}");
        await server.FlowOrchestrator.DidNotReceive().TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Teams_authorization_header_is_not_persisted_into_trigger_headers()
    {
        // Arrange — the Teams signature rides in Authorization; once the verifier can read it, it must still not
        // leak into the stored trigger headers (they are shown in run detail).
        var p = Providers["MicrosoftTeams"];
        using var server = NewServer();
        Register(server, p);
        ITriggerContext? captured = null;
        server.FlowOrchestrator.TriggerAsync(Arg.Do<ITriggerContext>(c => captured = c), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<object?>((object?)null));

        // Act
        var (status, body) = await SendAsync(server, p.Build(p.Slug, p.Key, new DeliveryOptions()));

        // Assert
        Assert.True(status == HttpStatusCode.OK, $"expected 200, got {(int)status} {body}");
        Assert.NotNull(captured);
        Assert.False(captured!.Trigger.Headers?.ContainsKey("Authorization") ?? false, "Authorization header persisted into trigger headers");
    }

    [Fact]
    public async Task Twilio_form_fields_reach_the_run_as_trigger_data()
    {
        // Arrange — a form-urlencoded Twilio delivery must start a run whose trigger body exposes the fields
        // (@triggerBody()?.Digits); any non-null representation is accepted here.
        var p = Providers["Twilio"];
        using var server = NewServer(sha1: true);
        Register(server, p);
        ITriggerContext? captured = null;
        server.FlowOrchestrator.TriggerAsync(Arg.Do<ITriggerContext>(c => captured = c), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<object?>((object?)null));

        // Act
        var (status, body) = await SendAsync(server, p.Build(p.Slug, p.Key, new DeliveryOptions()));

        // Assert
        Assert.True(status == HttpStatusCode.OK, $"expected 200, got {(int)status} {body}");
        Assert.NotNull(captured?.Trigger.Data);
        Assert.Contains("1234", System.Text.Json.JsonSerializer.Serialize(captured!.Trigger.Data), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_chunked_body_without_content_length_is_rejected_413()
    {
        // Arrange — the Content-Length pre-check cannot fire for chunked transfer; the streaming cap must.
        using var server = DashboardTestServer.WithServices(s => s.AddFlowDashboard(o => o.UseWebhookSecurity(b => b
            .UseEnforcementMode(WebhookEnforcementMode.Enforce).UseMaxBodyBytes(16 * 1024))));
        var p = Providers["GitHub"];
        Register(server, p);
        var req = new HttpRequestMessage(HttpMethod.Post, $"/flows/api/webhook/{p.Slug}")
        {
            Content = new StreamContent(new MemoryStream(new byte[64 * 1024])),
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        req.Headers.TransferEncodingChunked = true;

        // Act
        var (status, body) = await SendAsync(server, req);

        // Assert
        Assert.True(status == HttpStatusCode.RequestEntityTooLarge, $"expected 413, got {(int)status} {body}");
        await server.FlowOrchestrator.DidNotReceive().TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Request body that yields some bytes and then throws what Kestrel throws when the client closes the socket
    /// before Content-Length bytes arrived (<see cref="BadHttpRequestException"/> "Unexpected end of request content").
    /// </summary>
    private sealed class TruncatedBodyStream(int bytesBeforeFailure) : Stream
    {
        private int _remaining = bytesBeforeFailure;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
                throw new BadHttpRequestException("Unexpected end of request content.", StatusCodes.Status400BadRequest);
            var n = Math.Min(_remaining, buffer.Length);
            buffer.Span[..n].Fill((byte)' ');
            _remaining -= n;
            return ValueTask.FromResult(n);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TruncatingStartupFilter(ExceptionProbe probe) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nextMw) =>
            {
                ctx.Request.Body = new TruncatedBodyStream(1024);
                try { await nextMw(ctx); }
                catch (Exception ex) { probe.Escaped = ex; throw; }
                finally { probe.Completed.TrySetResult(); }
            });
            next(app);
        };
    }

    [Fact]
    public async Task Truncated_body_like_kestrel_reports_on_disconnect_starts_no_run_and_does_not_escape()
    {
        // Arrange — Kestrel surfaces a client that disconnects mid-body as BadHttpRequestException from
        // Request.Body.ReadAsync. The endpoint must answer (or drop) quietly, not 500 with an unhandled exception.
        // Production environment: in Development the auto-added developer exception page would swallow the
        // exception before the probe sees it, hiding exactly what this test looks for.
        var probe = new ExceptionProbe();
        using var server = NewServer(extra: s => s.AddSingleton<IStartupFilter>(new TruncatingStartupFilter(probe)), environment: "Production");
        var p = Providers["GitHub"];
        Register(server, p);
        var req = Post($"/flows/api/webhook/{p.Slug}", new byte[64 * 1024], "application/json");

        // Act — TestServer rethrows a server-side unhandled exception on the client call; swallow it here and
        // let the probe report it.
        HttpStatusCode? status = null;
        try { (status, _) = await SendAsync(server, req); }
        catch (Exception) { /* reported through probe.Escaped */ }
        await probe.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        await server.FlowOrchestrator.DidNotReceive().TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
        Assert.True(probe.Escaped is null, $"endpoint let {probe.Escaped?.GetType().Name} escape: {probe.Escaped?.Message}");
        Assert.NotEqual(HttpStatusCode.InternalServerError, status);
    }

    /// <summary>Request content that writes part of a body, then stalls until the client gives up.</summary>
    private sealed class StallingContent : HttpContent
    {
        public TaskCompletionSource PartWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(new byte[1024], cancellationToken);
            await stream.FlushAsync(cancellationToken);
            PartWritten.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 64 * 1024;
            return true;
        }
    }

    [Fact]
    public async Task Client_disconnect_mid_body_starts_no_run_and_does_not_escape_as_unhandled_exception()
    {
        // Arrange — the client sends 1 KiB of a declared 64 KiB body and then disconnects; the host signals
        // HttpContext.RequestAborted (TestServer aborts the request when the client cancels, as Kestrel does on
        // a dropped socket). CLAUDE.md: an aborted request must not surface as an unhandled exception.
        var probe = new ExceptionProbe();
        using var server = NewServer(extra: s => s.AddSingleton<IStartupFilter>(new ExceptionProbeStartupFilter(probe)));
        var p = Providers["GitHub"];
        Register(server, p);
        var content = new StallingContent();
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var req = new HttpRequestMessage(HttpMethod.Post, $"/flows/api/webhook/{p.Slug}") { Content = content };
        using var client = server.CreateClient();
        using var cts = new CancellationTokenSource();

        // Act
        var send = client.SendAsync(req, cts.Token);
        await content.PartWritten.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();
        try { using var _ = await send; }
        catch (OperationCanceledException) { /* expected: the client gave up */ }
        await probe.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        await server.FlowOrchestrator.DidNotReceive().TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
        Assert.True(probe.Escaped is null, $"endpoint let {probe.Escaped?.GetType().Name} escape: {probe.Escaped?.Message}");
    }
}
