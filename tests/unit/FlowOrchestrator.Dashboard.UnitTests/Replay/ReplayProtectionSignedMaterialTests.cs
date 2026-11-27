using System.Text;
using FlowOrchestrator.Dashboard.Webhooks.Replay;
using Microsoft.Extensions.Time.Testing;

namespace FlowOrchestrator.Dashboard.UnitTests.Replay;

/// <summary>
/// Pins that, once a signature gate verified a request, <see cref="ReplayProtectionGate"/> judges freshness
/// and derives the replay key only from material the signature covered. Unsigned timestamp / nonce headers
/// are chosen by whoever replays a captured delivery, so they must not influence the verdict.
/// </summary>
public sealed class ReplayProtectionSignedMaterialTests
{
    private static readonly Guid Flow = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private const string Trigger = "webhook";
    private const long Now = 1_700_000_000;

    private static readonly ReadOnlyMemory<byte> Body = Encoding.UTF8.GetBytes("{\"id\":\"evt_1\"}");

    private static ReplayProtectionGate NewGate() =>
        new(new InMemoryWebhookReplayStore(), new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(Now)));

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] pairs)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in pairs)
        {
            headers[name] = value;
        }
        return headers;
    }

    [Fact]
    public async Task Stale_signed_timestamp_is_rejected_even_when_an_unsigned_header_claims_it_is_fresh()
    {
        // Arrange — a captured Stripe delivery signed an hour ago, replayed with a forged fresh header.
        var gate = NewGate();
        var headers = Headers(("X-Webhook-Timestamp", Now.ToString()));

        // Act
        var verdict = await gate.EvaluateAsync(Flow, Trigger, headers, Body, toleranceSeconds: 300,
            timestampHeader: null, nonceHeader: null,
            signatureVerified: true, authenticatedTimestamp: (Now - 3600).ToString(), authenticatedNonce: null);

        // Assert
        Assert.Equal(ReplayProtectionGate.Decision.SkewRejected, verdict.Decision);
    }

    [Fact]
    public async Task Signed_timestamp_is_used_when_no_timestamp_header_is_sent()
    {
        // Arrange — a genuine Stripe delivery carries its timestamp only inside Stripe-Signature.
        var gate = NewGate();

        // Act
        var verdict = await gate.EvaluateAsync(Flow, Trigger, Headers(), Body, toleranceSeconds: 300,
            timestampHeader: null, nonceHeader: null,
            signatureVerified: true, authenticatedTimestamp: Now.ToString(), authenticatedNonce: null);

        // Assert
        Assert.Equal(ReplayProtectionGate.Decision.Fresh, verdict.Decision);
    }

    [Fact]
    public async Task Replay_with_varied_unsigned_headers_is_rejected_when_the_timestamp_is_signed()
    {
        // Arrange
        var gate = NewGate();
        var signedTs = Now.ToString();

        // Act
        var first = await gate.EvaluateAsync(Flow, Trigger,
            Headers(("X-Webhook-Timestamp", Now.ToString()), ("X-Delivery", "a")), Body, 300,
            timestampHeader: null, nonceHeader: "X-Delivery",
            signatureVerified: true, authenticatedTimestamp: signedTs, authenticatedNonce: null);
        var replay = await gate.EvaluateAsync(Flow, Trigger,
            Headers(("X-Webhook-Timestamp", (Now - 1).ToString()), ("X-Delivery", "b")), Body, 300,
            timestampHeader: null, nonceHeader: "X-Delivery",
            signatureVerified: true, authenticatedTimestamp: signedTs, authenticatedNonce: null);

        // Assert
        Assert.Equal(ReplayProtectionGate.Decision.Fresh, first.Decision);
        Assert.Equal(ReplayProtectionGate.Decision.ReplayRejected, replay.Decision);
    }

    [Fact]
    public async Task Body_only_signature_replay_is_rejected_despite_a_new_unsigned_timestamp_and_nonce()
    {
        // Arrange — GitHub-style scheme: the signature covers only the body, there is no signed timestamp.
        var gate = NewGate();

        // Act
        var first = await gate.EvaluateAsync(Flow, Trigger,
            Headers(("X-Webhook-Timestamp", Now.ToString()), ("X-GitHub-Delivery", "d-1")), Body, 300,
            timestampHeader: null, nonceHeader: "X-GitHub-Delivery",
            signatureVerified: true, authenticatedTimestamp: null, authenticatedNonce: null);
        var replay = await gate.EvaluateAsync(Flow, Trigger,
            Headers(("X-Webhook-Timestamp", (Now - 5).ToString()), ("X-GitHub-Delivery", "d-2")), Body, 300,
            timestampHeader: null, nonceHeader: "X-GitHub-Delivery",
            signatureVerified: true, authenticatedTimestamp: null, authenticatedNonce: null);

        // Assert
        Assert.Equal(ReplayProtectionGate.Decision.Fresh, first.Decision);
        Assert.Equal(ReplayProtectionGate.Decision.ReplayRejected, replay.Decision);
    }

    [Fact]
    public async Task Signed_token_is_the_replay_key_when_the_scheme_signs_one()
    {
        // Arrange — Mailgun signs timestamp + token, not the body: a replayed token with a new body is a replay.
        var gate = NewGate();
        var signedTs = Now.ToString();

        // Act
        var first = await gate.EvaluateAsync(Flow, Trigger, Headers(), Body, 300, null, null,
            signatureVerified: true, authenticatedTimestamp: signedTs, authenticatedNonce: "tok-1");
        var replay = await gate.EvaluateAsync(Flow, Trigger, Headers(), Encoding.UTF8.GetBytes("{\"other\":1}"), 300, null, null,
            signatureVerified: true, authenticatedTimestamp: signedTs, authenticatedNonce: "tok-1");

        // Assert
        Assert.Equal(ReplayProtectionGate.Decision.Fresh, first.Decision);
        Assert.Equal(ReplayProtectionGate.Decision.ReplayRejected, replay.Decision);
    }

    [Fact]
    public async Task Body_only_signature_without_any_timestamp_header_is_fresh_and_its_replay_rejected()
    {
        // Arrange — GitHub / Shopify / Linear: the publisher sends no timestamp header at all.
        var gate = NewGate();

        // Act
        var first = await gate.EvaluateAsync(Flow, Trigger, Headers(), Body, toleranceSeconds: 300,
            timestampHeader: null, nonceHeader: "X-GitHub-Delivery",
            signatureVerified: true, authenticatedTimestamp: null, authenticatedNonce: null);
        var replay = await gate.EvaluateAsync(Flow, Trigger, Headers(("X-GitHub-Delivery", "other")), Body, toleranceSeconds: 300,
            timestampHeader: null, nonceHeader: "X-GitHub-Delivery",
            signatureVerified: true, authenticatedTimestamp: null, authenticatedNonce: null);

        // Assert
        Assert.Equal(ReplayProtectionGate.Decision.Fresh, first.Decision);
        Assert.Equal(ReplayProtectionGate.Decision.ReplayRejected, replay.Decision);
    }

    [Fact]
    public async Task Body_only_signature_ignores_a_stale_unsigned_timestamp_header()
    {
        // Arrange — the unsigned header proves nothing about freshness either way.
        var gate = NewGate();
        var headers = Headers(("X-Webhook-Timestamp", (Now - 3600).ToString()));

        // Act
        var verdict = await gate.EvaluateAsync(Flow, Trigger, headers, Body, toleranceSeconds: 300,
            timestampHeader: null, nonceHeader: null,
            signatureVerified: true, authenticatedTimestamp: null, authenticatedNonce: null);

        // Assert
        Assert.Equal(ReplayProtectionGate.Decision.Fresh, verdict.Decision);
    }

    [Fact]
    public async Task Unsigned_delivery_still_needs_a_timestamp()
    {
        // Arrange — no signature scheme: the timestamp header is the only freshness signal there is.
        var gate = NewGate();

        // Act
        var verdict = await gate.EvaluateAsync(Flow, Trigger, Headers(), Body, toleranceSeconds: 300,
            timestampHeader: null, nonceHeader: null);

        // Assert
        Assert.Equal(ReplayProtectionGate.Decision.TimestampMissing, verdict.Decision);
    }

    [Theory]
    [InlineData("99999999999999")]
    [InlineData("-99999999999999")]
    public async Task Out_of_range_timestamp_is_rejected_instead_of_throwing(string timestamp)
    {
        // Arrange
        var gate = NewGate();

        // Act
        var verdict = await gate.EvaluateAsync(Flow, Trigger, Headers(("X-Webhook-Timestamp", timestamp)), Body,
            toleranceSeconds: 300, timestampHeader: null, nonceHeader: null);

        // Assert
        Assert.Equal(ReplayProtectionGate.Decision.TimestampMissing, verdict.Decision);
        Assert.Equal("invalid_timestamp", verdict.Reason);
    }
}
