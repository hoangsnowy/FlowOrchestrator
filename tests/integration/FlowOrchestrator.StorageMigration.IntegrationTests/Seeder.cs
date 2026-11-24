using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.StorageMigration.IntegrationTests;

/// <summary>What the seed produced, for assertions that need specific ids.</summary>
public sealed class SeedInfo
{
    public Guid BigOrderRunId { get; set; }
    public Guid SmallOrderRunId { get; set; }
    public Guid FailedRunId { get; set; }
    public Guid CancelledRunId { get; set; }
    public Guid ApprovalRunId { get; set; }
    public Guid NightlyRunId { get; set; }
    public List<(Guid FlowId, string Key)> IdempotencyKeys { get; } = [];
    public string DeepSearchNeedle { get; } = "zebra-needle-" + Guid.NewGuid().ToString("N")[..8];
    public Guid ValidNonceFlow { get; } = Guid.NewGuid();
    public const string ValidNonce = "nonce-still-valid-✓";
    public const string ExpiredNonce = "nonce-expired";
}

/// <summary>
/// Produces source data the way production does: real flows through the real engine and stores — completed,
/// failed (with a retried attempt), cancelled mid-run, When-skipped, ForEach, WaitForSignal delivered,
/// idempotency keys — plus schedule overrides / pause, a disabled flow and webhook log rows written through
/// the store APIs. Values include unicode, ~200 KB payloads and nulls.
/// </summary>
public static class Seeder
{
    public static async Task<SeedInfo> SeedAsync(FlowHost host)
    {
        var seed = new SeedInfo();
        var services = host.Services;
        var bigPayload = string.Concat(Enumerable.Repeat("Lorem ipsum ✓ 日本語 😀 — ", 8_000));

        // Completed: ForEach x3, When clause true, unicode + large payload, idempotency key, trigger headers.
        var idempotencyKey = "order-42-✓";
        (seed.BigOrderRunId, var bigStatus) = await host.RunAsync<OrderFlow>(
            new { label = "Grosse Bestellung — Tiếng Việt " + seed.DeepSearchNeedle, amount = 5000, items = new[] { "a", "b", "c" }, payload = bigPayload },
            new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey, ["X-Tenant"] = "tenant-ñ", ["X-Empty"] = "" });
        Assert.Equal("Succeeded", bigStatus);
        seed.IdempotencyKeys.Add((new OrderFlow().Id, idempotencyKey));

        // The duplicate trigger resolves to the same run and creates nothing.
        var duplicate = await host.TriggerAsync<OrderFlow>(new { amount = 1 }, new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey });
        Assert.Equal(seed.BigOrderRunId, duplicate);

        // Completed with a When-skipped step and an empty ForEach.
        (seed.SmallOrderRunId, _) = await host.RunAsync<OrderFlow>(new { label = "small", amount = 10, items = Array.Empty<string>() });

        // Failed, then retried (a second attempt that fails again).
        (seed.FailedRunId, var failedStatus) = await host.RunAsync<FailingFlow>(new { reason = default(string) });
        Assert.Equal("Failed", failedStatus);
        var retried = host.Events.Next(seed.FailedRunId);
        using (var scope = services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IFlowOrchestrator>().RetryStepAsync(new FailingFlow().Id, seed.FailedRunId, "boom");
        Assert.Equal("Failed", await retried.WaitAsync(TimeSpan.FromSeconds(60)));

        // Cancelled while its first step is executing.
        seed.CancelledRunId = await host.TriggerAsync<GateFlow>(null);
        await host.Gates.Entered(seed.CancelledRunId).WaitAsync(TimeSpan.FromSeconds(60));
        await services.GetRequiredService<IFlowRunControlStore>().RequestCancelAsync(seed.CancelledRunId, "operator cancelled — ✋ stop");
        host.Gates.Release(seed.CancelledRunId);
        Assert.Equal("Cancelled", await host.CompletedAsync(seed.CancelledRunId));

        // WaitForSignal, delivered.
        seed.ApprovalRunId = await host.TriggerAsync<ApprovalFlow>(new { requester = "Zoë" });
        await host.Waiters.Registered(seed.ApprovalRunId);
        var delivery = await services.GetRequiredService<IFlowSignalDispatcher>()
            .DispatchAsync(seed.ApprovalRunId, "approval", """{"approved":true,"approver":"Zoë 👩‍💼"}""");
        Assert.Equal(SignalDeliveryStatus.Delivered, delivery.Status);
        Assert.Equal("Succeeded", await host.CompletedAsync(seed.ApprovalRunId));

        // A cron flow: run once by hand, then paused with an override, then disabled.
        (seed.NightlyRunId, _) = await host.RunAsync<NightlyFlow>(null);
        var schedules = services.GetRequiredService<IFlowScheduleStateStore>();
        var nightly = new NightlyFlow();
        await schedules.SaveAsync(new FlowScheduleState
        {
            JobId = RecurringTriggerJobId.Create(nightly.Id, "nightly"),
            FlowId = nightly.Id,
            FlowName = nameof(NightlyFlow),
            TriggerKey = "nightly",
            IsPaused = true,
            CronOverride = "30 2 * * *",
            UpdatedAtUtc = DateTimeOffset.UtcNow.AddTicks(3),
        });
        await schedules.SaveAsync(new FlowScheduleState
        {
            JobId = RecurringTriggerJobId.Create(new OrderFlow().Id, "legacy-cron"),
            FlowId = new OrderFlow().Id,
            FlowName = nameof(OrderFlow),
            TriggerKey = "legacy-cron",
        });
        await services.GetRequiredService<IFlowStore>().SetEnabledAsync(nightly.Id, false);

        // Webhook delivery / rejection log: accepted, rejected with nulls, unicode + long body.
        var rejections = services.GetRequiredService<IWebhookRejectionStore>();
        await rejections.WriteAsync(new WebhookRejectionRecord
        {
            FlowId = new OrderFlow().Id, TriggerKey = "manual", ReceivedAt = DateTimeOffset.UtcNow.AddTicks(7), RemoteIp = "10.0.0.7",
            Reason = "accepted", StatusCode = 202, BodyBytes = 42, BodyTruncated = "{\"ok\":true}", HeadersJson = "{\"X-Sig\":\"abc\"}",
            Scheme = "hmac-sha256", ProcessingMs = 12.75, IsAccepted = true,
        });
        await rejections.WriteAsync(new WebhookRejectionRecord
        {
            ReceivedAt = DateTimeOffset.UtcNow, Reason = "unknown_slug", StatusCode = 404, BodyBytes = 0,
        });
        await rejections.WriteAsync(new WebhookRejectionRecord
        {
            FlowId = new OrderFlow().Id, TriggerKey = "manual", ReceivedAt = DateTimeOffset.UtcNow, RemoteIp = "2001:db8::1",
            Reason = "signature_mismatch", StatusCode = 401, BodyBytes = bigPayload.Length, BodyTruncated = bigPayload[..4096] + " — Ωmega",
            HeadersJson = "{\"User-Agent\":\"Ünïcödé/1.0\"}", Scheme = "stripe", ProcessingMs = 0.001,
        });

        // Replay nonces: one still valid (copied), one expired (not copied).
        var replay = services.GetRequiredService<IWebhookReplayStore>();
        Assert.True(await replay.TryRegisterAsync(seed.ValidNonceFlow, "manual", SeedInfo.ValidNonce, DateTimeOffset.UtcNow.AddHours(6)));
        Assert.True(await replay.TryRegisterAsync(seed.ValidNonceFlow, "manual", SeedInfo.ExpiredNonce, DateTimeOffset.UtcNow.AddHours(-6)));

        return seed;
    }
}
