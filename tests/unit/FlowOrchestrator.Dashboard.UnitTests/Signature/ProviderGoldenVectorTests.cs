using System.Text;
using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.UnitTests.Signature;

/// <summary>
/// Golden-vector tests for every built-in <see cref="WebhookSignatureScheme"/> dialect, driven by each
/// provider's OWN published example (secret, payload, headers, expected signature) wherever one exists.
/// </summary>
/// <remarks>
/// <para>
/// The older per-dialect vector tests compute the expected digest with the same helper the verifier uses,
/// so they prove only that the verifier agrees with itself. These vectors are hard-coded strings: the
/// "doc" ones are copied verbatim from the provider page cited on each entry; the "derived" ones were
/// computed once with an independent implementation (Python <c>hmac</c>) strictly from the provider's
/// documented algorithm and pasted in, because the provider publishes no worked example.
/// </para>
/// <para>
/// Every dialect gets four checks: the vector is accepted, a tampered payload is rejected, a wrong
/// secret is rejected, and a request without the signature is rejected. Headers are the ones the provider
/// actually sends — nothing extra (no <c>X-Webhook-Key</c>, no <c>X-Webhook-Timestamp</c>).
/// </para>
/// </remarks>
public sealed class ProviderGoldenVectorTests
{
    /// <summary>One provider example: everything the verifier sees for a single delivery.</summary>
    /// <param name="Dialect">Scheme name as written in a manifest (<c>webhookSignatureScheme</c>).</param>
    /// <param name="Spec">Spec the verifier runs with.</param>
    /// <param name="Key">The secret exactly as the provider's console shows it.</param>
    /// <param name="Body">Raw body bytes as sent.</param>
    /// <param name="Headers">The headers the provider sends.</param>
    /// <param name="SignatureHeader">Header carrying the signature (removed by the "missing" check).</param>
    /// <param name="AbsoluteUrl">Request URL the provider signed (Twilio, Square).</param>
    /// <param name="FormFields">Parsed form fields the provider signed (Twilio).</param>
    /// <param name="AllowLegacySha1">Whether the host must allow HMAC-SHA1 for this dialect.</param>
    /// <param name="Tamper">Returns a context whose signed material has been modified.</param>
    /// <param name="StripSignature">Returns a context with the signature removed.</param>
    public sealed record GoldenVector(
        string Dialect,
        WebhookSignatureSpec Spec,
        string Key,
        byte[] Body,
        IReadOnlyDictionary<string, string> Headers,
        string? SignatureHeader,
        string? AbsoluteUrl = null,
        IReadOnlyDictionary<string, string>? FormFields = null,
        bool AllowLegacySha1 = false,
        Func<WebhookSignatureContext, WebhookSignatureContext>? Tamper = null,
        Func<WebhookSignatureContext, WebhookSignatureContext>? StripSignature = null)
    {
        /// <summary>Builds the verifier context for this vector, optionally with another key.</summary>
        public WebhookSignatureContext ToContext(string? keyOverride = null) => new()
        {
            Body = Body,
            Headers = new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase),
            AbsoluteUrl = AbsoluteUrl,
            FormFields = FormFields,
            Spec = Spec,
            HmacKey = keyOverride ?? Key,
            AllowLegacySha1 = AllowLegacySha1,
        };

        /// <inheritdoc/>
        public override string ToString() => Dialect;
    }

    private static WebhookSignatureSpec Spec(WebhookSignatureScheme scheme) => PartnerSchemeRegistry.TryGet(scheme)!;

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static Dictionary<string, string> H(params (string Name, string Value)[] headers)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, v) in headers) d[n] = v;
        return d;
    }

    // ── Shared payloads ──────────────────────────────────────────────────────────────────────────

    // https://docs.slack.dev/authentication/verifying-requests-from-slack — the slash-command body of the worked example, verbatim.
    private const string SlackDocBody =
        "token=xyzz0WbapA4vBCDEFasx0q6G&team_id=T1DC2JH3J&team_domain=testteamnow&channel_id=G8PSS9T3V&channel_name=foobar&user_id=U2CERLKJA&user_name=roadrunner&command=%2Fwebhook-collect&text=&response_url=https%3A%2F%2Fhooks.slack.com%2Fcommands%2FT1DC2JH3J%2F397700885554%2F96rGlfmibIGlgcZRskXaIFfN&trigger_id=398738663015.47445629121.803a0bc887a14d10d2c447fce8b6703c";

    private const string StripeBody = "{\n  \"id\": \"evt_test_webhook\",\n  \"object\": \"event\"\n}";

    private const string MailgunBody =
        "{\"signature\":{\"token\":\"e0b5477167110d68991efc6b9f89f0a11066af27834600e123\",\"timestamp\":\"1770920772\",\"signature\":\"407c2da26368cd4d6005c3330fd3d4a324beae2a051ec418fd78a133be4ab3f5\"},\"event-data\":{\"event\":\"delivered\",\"id\":\"CPgfbmQMTCKtHW6uIWtuVe\"}}";

    /// <summary>Every built-in dialect with a hard-coded vector. Theory data source.</summary>
    public static TheoryData<string> Dialects
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var v in Vectors) data.Add(v.Key);
            return data;
        }
    }

    /// <summary>The vectors, keyed by dialect.</summary>
    public static readonly IReadOnlyDictionary<string, GoldenVector> Vectors = BuildVectors();

    private static Dictionary<string, GoldenVector> BuildVectors()
    {
        var list = new List<GoldenVector>
        {
            // DOC — https://docs.github.com/en/webhooks/using-webhooks/validating-webhook-deliveries
            // "Testing the webhook payload validation": secret / payload / X-Hub-Signature-256 verbatim.
            new("GitHub", Spec(WebhookSignatureScheme.GitHub),
                "It's a Secret to Everybody", Utf8("Hello, World!"),
                H(("X-Hub-Signature-256", "sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17")),
                "X-Hub-Signature-256"),

            // DERIVED — same GitHub inputs, legacy X-Hub-Signature is HMAC-SHA1 hex with a "sha1=" prefix
            // (GitHub sends it alongside X-Hub-Signature-256; the page above documents it as legacy).
            new("GitHubLegacy", Spec(WebhookSignatureScheme.GitHubLegacy),
                "It's a Secret to Everybody", Utf8("Hello, World!"),
                H(("X-Hub-Signature", "sha1=01dc10d0c83e72ed246219cdd91669667fe2ca59")),
                "X-Hub-Signature", AllowLegacySha1: true),

            // DOC — https://support.atlassian.com/bitbucket-cloud/docs/manage-webhooks/
            // "Testing the webhook payload validation": secret / payload / X-Hub-Signature verbatim.
            new("Bitbucket", Spec(WebhookSignatureScheme.Bitbucket),
                "It's a Secret to Everybody", Utf8("Hello World!"),
                H(("X-Hub-Signature", "sha256=a4771c39fbe90f317c7824e83ddef3caae9cb3d976c214ace1f2937e133263c9")),
                "X-Hub-Signature"),

            // DOC — https://developer.atlassian.com/cloud/jira/platform/webhooks/
            // "Testing the webhook payload validation" (Jira Cloud): identical published vector.
            new("Atlassian", Spec(WebhookSignatureScheme.Atlassian),
                "It's a Secret to Everybody", Utf8("Hello World!"),
                H(("X-Hub-Signature", "sha256=a4771c39fbe90f317c7824e83ddef3caae9cb3d976c214ace1f2937e133263c9")),
                "X-Hub-Signature"),

            // DERIVED signature, DOC header layout — https://docs.stripe.com/webhooks#verify-manually.
            // The header shape is the documented example verbatim (t, two v1 entries, sandbox v0, and the
            // unsigned secret/destination/payload debug entries that "your integration must accept"); only
            // the v1 digests are computed over {t}.{body} because Stripe publishes no secret for that example.
            new("Stripe", Spec(WebhookSignatureScheme.Stripe),
                "whsec_5f8e2d1c0b9a8f7e6d5c4b3a2f1e0d9c", Utf8(StripeBody),
                H(("Stripe-Signature",
                    "t=1798765432," +
                    "v1=64db4cd0175a6b76314adcbdf22a0f1df3b9dcfa98d45352355583fcbdb09214," +
                    "v1=d64f6ddcb979f547526080a22762693ce681d4506ea06deac6b5546623d2fb54," +
                    "v0=6ffbb59b2300aae63f272406069a9788598b792a944a07aba816edb039989a39," +
                    "secret=whsec_****************************PzVt," +
                    "destination=we_1TN7mG6oEAX7HoXLZoZugElv," +
                    "payload=141b998e")),
                "Stripe-Signature"),

            // DOC — https://docs.slack.dev/authentication/verifying-requests-from-slack — signing secret,
            // X-Slack-Request-Timestamp, slash-command body and v0= signature all verbatim.
            new("Slack", Spec(WebhookSignatureScheme.Slack),
                "8f742231b10e8888abcd99yyyzzz85a5", Utf8(SlackDocBody),
                H(("X-Slack-Request-Timestamp", "1531420618"),
                  ("X-Slack-Signature", "v0=a2114d57b48eac39b9ad189dd8316235a7b4a8d21a10bd27519666489c69b503")),
                "X-Slack-Signature"),

            // DERIVED — https://shopify.dev/docs/apps/build/webhooks/subscribe/https "HMAC verification":
            // base64 HMAC-SHA256 of the raw body with the app client secret, no prefix.
            new("Shopify", Spec(WebhookSignatureScheme.Shopify),
                "shpss_test_client_secret", Utf8("{\"id\":820982911946154508,\"email\":\"jon@doe.ca\"}"),
                H(("X-Shopify-Hmac-SHA256", "gTo8vonDzCI2gW75VIT1BSi376o18eTne0zyX8fJg6U="),
                  ("X-Shopify-Topic", "orders/create"),
                  ("X-Shopify-Webhook-Id", "b54557e4-bdd9-4b37-8a5f-bf7d70bcd043")),
                "X-Shopify-Hmac-SHA256"),

            // DOC — https://www.twilio.com/docs/usage/security "Explore the algorithm yourself": AuthToken 12345,
            // URL with query string, the five POST fields and L/OH5YylLD5NRKLltdqwSvS0BnU= verbatim.
            new("Twilio", Spec(WebhookSignatureScheme.Twilio),
                "12345",
                Utf8("Digits=1234&To=%2B18005551212&From=%2B14158675310&Caller=%2B14158675310&CallSid=CA1234567890ABCDE"),
                H(("X-Twilio-Signature", "L/OH5YylLD5NRKLltdqwSvS0BnU=")),
                "X-Twilio-Signature",
                AbsoluteUrl: "https://example.com/myapp.php?foo=1&bar=2",
                FormFields: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Digits"] = "1234",
                    ["To"] = "+18005551212",
                    ["From"] = "+14158675310",
                    ["Caller"] = "+14158675310",
                    ["CallSid"] = "CA1234567890ABCDE",
                },
                AllowLegacySha1: true,
                Tamper: ctx => ctx with
                {
                    FormFields = new Dictionary<string, string>(ctx.FormFields!, StringComparer.Ordinal) { ["Digits"] = "9999" },
                }),

            // DERIVED — https://developer.squareup.com/docs/webhooks/step3validate: base64 HMAC-SHA256 over
            // {notification URL}{raw body} with the subscription signature key.
            new("Square", Spec(WebhookSignatureScheme.Square),
                "sq0wss-test-signature-key",
                Utf8("{\"merchant_id\":\"MLEFBHHSJGVHD\",\"type\":\"payment.updated\",\"event_id\":\"ac3ac95b-f97d-458c-a6e6-18981597e05f\"}"),
                H(("x-square-hmacsha256-signature", "I7rM/omMPg5uoC8U5kVLISY1wbZ7bWqKOtZm60UAjfE=")),
                "x-square-hmacsha256-signature",
                AbsoluteUrl: "https://example.com/webhook"),

            // DERIVED signature, DOC headers — https://developers.zoom.us/docs/api/webhooks/ "Verify with Zoom's
            // header": x-zm-request-timestamp 1739923528 and the documented header set (including the
            // verification-token "authorization" header Zoom also sends); v0= hex over v0:{ts}:{body}.
            new("Zoom", Spec(WebhookSignatureScheme.Zoom),
                "zoom_secret_token",
                Utf8("{\"event\":\"meeting.started\",\"event_ts\":1626230691572,\"payload\":{\"account_id\":\"Wk9PTV9BQ0NPVU5UX0lE\"}}"),
                H(("authorization", "Wk9PTV9BVVRIT1JJWkFUSU9O"),
                  ("x-zm-request-timestamp", "1739923528"),
                  ("x-zm-signature", "v0=faa77778ffdbc2e878da813db42cd4f0d24409688212fee794551a9e95ddb541"),
                  ("x-zm-request-id", "6009d653_d487_445d_8406_42b654974899")),
                "x-zm-signature"),

            // DERIVED signature, DOC headers — https://linear.app/developers/webhooks: Linear-Delivery /
            // Linear-Event / Linear-Signature / Linear-Timestamp (milliseconds); hex HMAC-SHA256 of the body.
            new("Linear", Spec(WebhookSignatureScheme.Linear),
                "lin_wh_test_secret",
                Utf8("{\"action\":\"create\",\"type\":\"Issue\",\"webhookTimestamp\":1676056940508}"),
                H(("Linear-Delivery", "234d1a4e-b617-4388-90fe-adc3633d6b72"),
                  ("Linear-Event", "Issue"),
                  ("Linear-Signature", "c2a5cb7a103f68b326d2879fbeb9594c159485c04622dc094a5fa0bc99b68a84"),
                  ("Linear-Timestamp", "1676056940508")),
                "Linear-Signature"),

            // DERIVED signature, DOC payload — https://www.dropbox.com/developers/reference/webhooks: the
            // notification body example verbatim; hex HMAC-SHA256 of the body with the app secret.
            new("Dropbox", Spec(WebhookSignatureScheme.Dropbox),
                "dbx_app_secret",
                Utf8("{\"list_folder\": {\"accounts\": [\"dbid:AAH4f99T0taONIb-OurWxbNQ6ywGRopQngc\", \"dbid:AAH4f99T0taONIb-OurWxbNQ6ywGRopQngc\"]}, \"delta\": {\"users\": [12345678, 23456789]}}"),
                H(("X-Dropbox-Signature", "a9c2b5455e2ac6bdf6fdecce56efdd11c54becb75f32951ad653438971d29002")),
                "X-Dropbox-Signature"),

            // DERIVED signature, DOC token/timestamp — https://documentation.mailgun.com/docs/mailgun/user-manual/webhooks/securing-webhooks
            // The signature block's token + timestamp are the documented example; Mailgun signs {timestamp}{token}
            // (not the body), so "tamper" alters the token and "missing" drops the signature field.
            new("Mailgun", Spec(WebhookSignatureScheme.Mailgun),
                "mailgun-webhook-signing-key", Utf8(MailgunBody),
                H(),
                SignatureHeader: null,
                Tamper: ctx => ctx with { Body = Utf8(MailgunBody.Replace("e0b5477167110d68991efc6b9f89f0a11066af27834600e123", "f0b5477167110d68991efc6b9f89f0a11066af27834600e123", StringComparison.Ordinal)) },
                StripSignature: ctx => ctx with { Body = Utf8(MailgunBody.Replace(",\"signature\":\"407c2da26368cd4d6005c3330fd3d4a324beae2a051ec418fd78a133be4ab3f5\"", string.Empty, StringComparison.Ordinal)) }),

            // DERIVED — https://learn.microsoft.com/en-us/microsoftteams/platform/webhooks-and-connectors/how-to/add-outgoing-webhook
            // "Base64-decode the security token provided by Teams ... and use the resulting bytes as the HMAC
            // key"; base64 HMAC-SHA256 of the UTF-8 body in "Authorization: HMAC <sig>". The key below is the
            // token exactly as Teams displays it (base64 of 32 bytes). Signing with the token's UTF-8 bytes
            // instead would give ZrPgiegq/rOfc0NNcMAyYG82PDJgDPlZrt5a7Je/30o= — that is NOT what Teams sends.
            new("MicrosoftTeams", Spec(WebhookSignatureScheme.MicrosoftTeams),
                "6fMbcEmK8Zdzj5A9xXk1yW3m7Hn2pQ4rT0uVwY8zB1c=",
                Utf8("{\"type\":\"message\",\"id\":\"1485983408511\",\"timestamp\":\"2017-02-01T21:10:07.437Z\",\"text\":\"<at>Sample</at> hello\"}"),
                H(("Authorization", "HMAC z+UQ1cvNUjvPJCBQEhbZcm7X/bYPy/LdJW0xMBlDNFQ=")),
                "Authorization"),

            // DERIVED — Calendly webhook signatures (developer.calendly.com/api-docs, "Webhook Signatures";
            // the page is a JS-rendered app and not retrievable as text): Calendly-Webhook-Signature
            // t=…,v1=… with hex HMAC-SHA256 over {t}.{body}, same construction as Stripe.
            new("Calendly", Spec(WebhookSignatureScheme.Calendly),
                "calendly_signing_key",
                Utf8("{\"event\":\"invitee.created\",\"payload\":{\"email\":\"test@example.com\"}}"),
                H(("Calendly-Webhook-Signature", "t=1492774577,v1=369e97e9abd11dbbecb1ff8b34eb6f7a28257e7fba7b003dacd72c242068df7c")),
                "Calendly-Webhook-Signature"),

            // DERIVED — FlowOrchestrator's own Generic dialect (PartnerSchemeRegistry): X-Signature: sha256=<hex>.
            new("Generic", Spec(WebhookSignatureScheme.Generic),
                "generic-secret", Utf8("{\"event\":\"ping\"}"),
                H(("X-Signature", "sha256=adbb938347115a1b87cab58c7bc6980054aec92d3d79c686a74f97cd58a14cfd")),
                "X-Signature"),

            // DOC (via GitHub) — the Custom dialect assembled from manifest fields to describe GitHub's
            // wire format, fed GitHub's published vector. Proves the manifest-driven path, not just the registry.
            new("Custom", WebhookSignatureSpecResolver.Resolve(new Dictionary<string, object?>
                {
                    ["webhookSignatureScheme"] = "Custom",
                    ["webhookSignatureHeader"] = "X-Hub-Signature-256",
                    ["webhookSignatureAlgorithm"] = "Sha256",
                    ["webhookSignatureEncoding"] = "HexLower",
                    ["webhookSignaturePrefix"] = "sha256=",
                    ["webhookSignedPayloadStrategy"] = "RawBody",
                }, customSchemes: null)!,
                "It's a Secret to Everybody", Utf8("Hello, World!"),
                H(("X-Hub-Signature-256", "sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17")),
                "X-Hub-Signature-256"),
        };
        return list.ToDictionary(v => v.Dialect, StringComparer.Ordinal);
    }

    [Fact]
    public void Every_built_in_scheme_has_a_golden_vector()
    {
        // Arrange
        var expected = Enum.GetValues<WebhookSignatureScheme>()
            .Where(s => s != WebhookSignatureScheme.None)
            .Select(s => s.ToString())
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        // Act
        var covered = Vectors.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray();

        // Assert
        Assert.Equal(expected, covered);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Provider_vector_is_accepted(string dialect)
    {
        // Arrange
        var vector = Vectors[dialect];
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(vector.ToContext());

        // Assert
        Assert.True(result.IsValid, $"{dialect}: provider vector rejected with {result.Reason}");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Tampered_payload_is_rejected(string dialect)
    {
        // Arrange
        var vector = Vectors[dialect];
        var ctx = vector.ToContext();
        var tampered = vector.Tamper is not null
            ? vector.Tamper(ctx)
            : ctx with { Body = FlipLastByte(vector.Body) };
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(tampered);

        // Assert
        Assert.False(result.IsValid, $"{dialect}: tampered payload accepted");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Wrong_secret_is_rejected(string dialect)
    {
        // Arrange
        var vector = Vectors[dialect];
        // Same shape as the real key (Teams: still valid base64) so only the value differs.
        var wrongKey = dialect == "MicrosoftTeams" ? Convert.ToBase64String(new byte[32]) : vector.Key + "x";
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(vector.ToContext(wrongKey));

        // Assert
        Assert.False(result.IsValid, $"{dialect}: wrong secret accepted");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Missing_signature_is_rejected(string dialect)
    {
        // Arrange
        var vector = Vectors[dialect];
        var ctx = vector.ToContext();
        WebhookSignatureContext stripped;
        if (vector.StripSignature is not null)
        {
            stripped = vector.StripSignature(ctx);
        }
        else
        {
            var headers = new Dictionary<string, string>(vector.Headers, StringComparer.OrdinalIgnoreCase);
            headers.Remove(vector.SignatureHeader!);
            stripped = ctx with { Headers = headers };
        }
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(stripped);

        // Assert
        Assert.False(result.IsValid, $"{dialect}: request without signature accepted");
    }

    [Fact]
    public void Stripe_vector_authenticates_the_t_timestamp()
    {
        // Arrange — Stripe's timestamp is inside the digest, so the verifier must hand it to the replay gate.
        var vector = Vectors["Stripe"];
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(vector.ToContext());

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal("1798765432", result.AuthenticatedTimestamp);
    }

    [Fact]
    public void Stripe_vector_signed_only_by_the_rotated_out_secret_is_accepted_with_rotation_flag()
    {
        // Arrange — the second v1 entry in the documented header is the previous secret's signature
        // ("Stripe generates one signature for each secret" while rolling).
        var vector = Vectors["Stripe"];
        var ctx = vector.ToContext("whsec_some_unrelated_current_secret") with
        {
            HmacKeyPrevious = "whsec_0000previous0000secret0000",
        };
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid);
        Assert.True(result.UsedRotationKey);
    }

    [Fact]
    public void Stripe_sandbox_v0_signature_alone_is_not_accepted()
    {
        // Arrange — docs: "ignore signature schemes other than v1" (downgrade protection). A header whose
        // only valid digest sits in a v0 entry must fail.
        var vector = Vectors["Stripe"];
        var ctx = vector.ToContext() with
        {
            Headers = H(("Stripe-Signature",
                "t=1798765432,v0=64db4cd0175a6b76314adcbdf22a0f1df3b9dcfa98d45352355583fcbdb09214")),
        };
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Slack_events_api_json_delivery_is_accepted()
    {
        // Arrange — DERIVED: the Events API url_verification body (docs.slack.dev/apis/events-api) signed with
        // the documented signing secret and timestamp; Events API deliveries are JSON, slash commands are form.
        var body = Utf8("{\"token\":\"Jhj5dZrVaK7ZwHHjRyZWjbDl\",\"challenge\":\"3eZbrw1aBm2rZgRNFdxV2595E9CY3gmdALWMmHkvFXO7tYXAYM8P\",\"type\":\"url_verification\"}");
        var ctx = new WebhookSignatureContext
        {
            Body = body,
            Headers = H(("X-Slack-Request-Timestamp", "1531420618"),
                        ("X-Slack-Signature", "v0=f9bd80929259f3f6d5fe3a3186c453c89cd1ad4862815a15837584f31c993652")),
            Spec = Spec(WebhookSignatureScheme.Slack),
            HmacKey = "8f742231b10e8888abcd99yyyzzz85a5",
        };
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal("1531420618", result.AuthenticatedTimestamp);
    }

    [Fact]
    public void Twilio_signature_rejected_when_sha1_not_allowed_on_the_host()
    {
        // Arrange — characterisation: Twilio's default signing algorithm IS HMAC-SHA1, so a host that has not
        // called AllowLegacySha1() rejects every genuine Twilio delivery. Pinned so the coupling stays visible.
        var vector = Vectors["Twilio"];
        var ctx = vector.ToContext() with { AllowLegacySha1 = false };
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.Equal(WebhookSignatureFailureReason.AlgorithmNotAllowed, result.Reason);
    }

    [Fact]
    public void StandardWebhooks_vector_is_accepted_via_custom_spec_and_strategy()
    {
        // Arrange — DOC: Standard Webhooks (Svix) library test vector,
        // https://github.com/standard-webhooks/standard-webhooks/blob/main/libraries/python/tests/test_webhooks.py
        // secret whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw, msg_p5jXN8AQM9LWM0D4loKWxJek, ts 1614265330,
        // payload {"test": 2432232314} → "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=".
        // The spec (spec/standard-webhooks.md) signs "{webhook-id}.{webhook-timestamp}.{body}" with the
        // base64-DECODED part after "whsec_". Header shape and payload are expressible through a Custom spec
        // plus a custom strategy, but the HMAC key is always the UTF-8 bytes of the configured string, so
        // there is no configuration that verifies a Standard Webhooks delivery. This test asserts the
        // expected (accepting) behaviour and is expected to FAIL until key decoding is configurable.
        var spec = new WebhookSignatureSpec
        {
            HeaderName = "webhook-signature",
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = SignatureEncoding.Base64,
            MultiValueDelimiter = " ",
            KeyValueSeparator = ",",
            SignatureValueKey = "v1",
            TimestampHeaderName = "webhook-timestamp",
            RequireTimestamp = true,
            AcceptMultipleSignatures = true,
            SignedPayloadStrategy = SignedPayloadStrategy.Custom,
            CustomStrategyName = "standard-webhooks",
        };
        var strategies = new Dictionary<string, Func<WebhookSignatureContext, byte[]>>
        {
            ["standard-webhooks"] = c => Utf8(c.Headers["webhook-id"] + "." + c.Headers["webhook-timestamp"] + "." + Encoding.UTF8.GetString(c.Body.Span)),
        };
        var ctx = new WebhookSignatureContext
        {
            Body = Utf8("{\"test\": 2432232314}"),
            Headers = H(("webhook-id", "msg_p5jXN8AQM9LWM0D4loKWxJek"),
                        ("webhook-timestamp", "1614265330"),
                        ("webhook-signature", "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=")),
            Spec = spec,
            HmacKey = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw",
        };
        var verifier = new HmacSignatureVerifier(strategies);

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid, $"Standard Webhooks vector rejected with {result.Reason}");
    }

    private static byte[] FlipLastByte(byte[] body)
    {
        var copy = (byte[])body.Clone();
        copy[^1] ^= 0x01;
        return copy;
    }
}
