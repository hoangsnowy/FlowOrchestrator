using System.Globalization;
using System.Net;
using System.Text;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Replay-key compensation: a delivery that passes replay protection and then fails before its run is started
/// releases the key it recorded, so the publisher's retry is processed instead of being answered <c>409</c>.
/// </summary>
public sealed partial class ProviderWebhookEndpointTests
{
    /// <summary>Makes the first <c>TriggerAsync</c> throw (store outage) and every later call succeed.</summary>
    /// <param name="server">Server whose engine substitute is arranged.</param>
    /// <param name="markEntryStarted">Simulate an engine that dispatched an entry step before throwing.</param>
    private static void FailFirstTrigger(DashboardTestServer server, bool markEntryStarted = false)
    {
        var calls = 0;
        server.FlowOrchestrator.TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.Arg<ITriggerContext>();
                if (Interlocked.Increment(ref calls) == 1)
                {
                    if (markEntryStarted) ctx.EntryStepsStarted = true;
                    throw new InvalidOperationException("simulated run-store outage");
                }
                return new ValueTask<object?>(new FlowTriggerResult(ctx.RunId));
            });
    }

    /// <summary>Sends a request; an exception TestServer rethrows from the app counts as a 500.</summary>
    private static async Task<(HttpStatusCode Status, string Body, Exception? Error)> SendCatchingAsync(DashboardTestServer server, HttpRequestMessage req)
    {
        try
        {
            var (status, body) = await SendAsync(server, req);
            return (status, body, null);
        }
        catch (InvalidOperationException ex)
        {
            return (HttpStatusCode.InternalServerError, ex.Message, ex);
        }
    }

    [Fact]
    public async Task StandardWebhooks_retry_after_a_failed_trigger_is_processed_not_409()
    {
        // Arrange — the first attempt passes the gate (webhook-id recorded) and then the trigger throws (5xx).
        // The publisher retries the same message id, re-timestamped and re-signed.
        var p = Providers["StandardWebhooks"];
        using var server = NewServer(replaySeconds: 300);
        Register(server, p);
        FailFirstTrigger(server);
        var body = U("{\"type\":\"invoice.paid\",\"n\":3}");
        var first = StandardWebhooksRequest(p.Slug, p.Key, "msg_retry_me", Now, body, body);
        var retry = StandardWebhooksRequest(p.Slug, p.Key, "msg_retry_me", Now + 5, body, body);

        // Act
        var (s1, _, _) = await SendCatchingAsync(server, first);
        var (s2, b2, _) = await SendCatchingAsync(server, retry);

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, s1);
        Assert.True(s2 == HttpStatusCode.OK, $"retry after a failed trigger expected 200, got {(int)s2} {b2}");
        await server.FlowOrchestrator.Received(2).TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Body_only_redelivery_after_a_failed_trigger_is_processed_not_409()
    {
        // Arrange — GitHub "Redeliver" sends the byte-identical delivery; the body hash is the replay key.
        var p = Providers["GitHub"];
        using var server = NewServer(replaySeconds: 300);
        Register(server, p);
        FailFirstTrigger(server);
        var first = p.Build(p.Slug, p.Key, new DeliveryOptions());
        var redelivery = p.Build(p.Slug, p.Key, new DeliveryOptions());

        // Act
        var (s1, _, _) = await SendCatchingAsync(server, first);
        var (s2, b2, _) = await SendCatchingAsync(server, redelivery);

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, s1);
        Assert.True(s2 == HttpStatusCode.OK, $"redelivery after a failed trigger expected 200, got {(int)s2} {b2}");
    }

    [Fact]
    public async Task Genuine_replay_after_a_successful_delivery_is_still_409()
    {
        // Arrange — success keeps the key: compensation only runs on failure.
        var p = Providers["StandardWebhooks"];
        using var server = NewServer(replaySeconds: 300);
        Register(server, p);
        var body = U("{\"type\":\"invoice.paid\",\"n\":4}");
        var ts = Now;
        var genuine = StandardWebhooksRequest(p.Slug, p.Key, "msg_once", ts, body, body);
        var replay = StandardWebhooksRequest(p.Slug, p.Key, "msg_once", ts, body, body);

        // Act
        var (s1, b1, _) = await SendCatchingAsync(server, genuine);
        var (s2, b2, _) = await SendCatchingAsync(server, replay);

        // Assert
        Assert.True(s1 == HttpStatusCode.OK, $"genuine expected 200, got {(int)s1} {b1}");
        Assert.True(s2 == HttpStatusCode.Conflict, $"replay expected 409, got {(int)s2} {b2}");
        await server.FlowOrchestrator.Received(1).TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Trigger_that_failed_after_dispatching_an_entry_step_keeps_the_key()
    {
        // Arrange — the engine dispatched an entry step (the run is alive; recovery finishes it) and then threw.
        // Releasing the key would let the retry start a second run, so the retry stays a replay.
        var p = Providers["StandardWebhooks"];
        using var server = NewServer(replaySeconds: 300);
        Register(server, p);
        FailFirstTrigger(server, markEntryStarted: true);
        var body = U("{\"type\":\"invoice.paid\",\"n\":5}");
        var first = StandardWebhooksRequest(p.Slug, p.Key, "msg_half_started", Now, body, body);
        var retry = StandardWebhooksRequest(p.Slug, p.Key, "msg_half_started", Now + 5, body, body);

        // Act
        var (s1, _, _) = await SendCatchingAsync(server, first);
        var (s2, b2, _) = await SendCatchingAsync(server, retry);

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, s1);
        Assert.True(s2 == HttpStatusCode.Conflict, $"retry of a started run expected 409, got {(int)s2} {b2}");
        await server.FlowOrchestrator.Received(1).TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delivery_refused_by_the_legacy_key_check_does_not_burn_its_nonce()
    {
        // Arrange — unsigned trigger authenticated by the shared secret, replay key from X-Delivery-Id. A caller
        // without the secret used to burn any delivery id it chose (registered, then 401); the genuine delivery
        // with that id then got 409.
        using var server = NewServer(replaySeconds: 300);
        Register(server, "shared-events", new Dictionary<string, object?>
        {
            ["webhookSecret"] = "the-shared-secret",
            ["webhookNonceHeader"] = "X-Delivery-Id",
        });
        var ts = Now.ToString(CultureInfo.InvariantCulture);
        HttpRequestMessage Delivery(string key) => Post("/flows/api/webhook/shared-events", U("{\"order\":7}"), "application/json",
            ("X-Webhook-Key", key), ("X-Webhook-Timestamp", ts), ("X-Delivery-Id", "delivery-7"));

        // Act
        var (s1, _, _) = await SendCatchingAsync(server, Delivery("guessed-wrong"));
        var (s2, b2, _) = await SendCatchingAsync(server, Delivery("the-shared-secret"));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, s1);
        Assert.True(s2 == HttpStatusCode.OK, $"genuine delivery expected 200, got {(int)s2} {b2}");
    }

    [Fact]
    public async Task Failing_release_never_masks_the_original_trigger_failure()
    {
        // Arrange — the replay store cannot release (outage): the request still fails with the trigger's own
        // error, and the key simply stays until it expires.
        var p = Providers["StandardWebhooks"];
        using var server = NewServer(replaySeconds: 300, extra: s => s.AddSingleton<IWebhookReplayStore, ForgetFailsReplayStore>());
        Register(server, p);
        FailFirstTrigger(server);
        var body = U("{\"type\":\"invoice.paid\",\"n\":6}");

        // Act
        var (status, message, error) = await SendCatchingAsync(server, StandardWebhooksRequest(p.Slug, p.Key, "msg_x", Now, body, body));

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        if (error is not null)
            Assert.Equal("simulated run-store outage", message);
    }

    /// <summary>In-memory replay store whose release always throws.</summary>
    private sealed class ForgetFailsReplayStore : IWebhookReplayStore
    {
        private readonly FlowOrchestrator.Dashboard.Webhooks.Replay.InMemoryWebhookReplayStore _inner = new();

        public ValueTask<bool> TryRegisterAsync(Guid flowId, string triggerKey, string nonce, DateTimeOffset expiresAt, Guid registrationId, CancellationToken ct = default)
            => _inner.TryRegisterAsync(flowId, triggerKey, nonce, expiresAt, registrationId, ct);

        public ValueTask<bool> TryForgetAsync(Guid flowId, string triggerKey, string nonce, Guid registrationId, CancellationToken ct = default)
            => throw new TimeoutException("replay store unreachable");

        public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct = default) => _inner.PurgeExpiredAsync(now, ct);
    }
}
