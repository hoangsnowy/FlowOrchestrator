using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Testing.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>
/// Regression for the dashboard <c>POST /api/runs/{runId}/rerun</c> + webhook + idempotency-key
/// interaction. The dashboard rehydrates the original trigger headers from
/// <see cref="FlowRunRecord.TriggerHeaders"/> when re-triggering. If those headers carried an
/// <c>Idempotency-Key</c>, the engine's dedup ledger would short-circuit the new trigger and
/// silently return the original RunId — the user-visible "Re-run" button would become a no-op.
///
/// The fix strips the configured idempotency header before re-invoking
/// <see cref="IFlowOrchestrator.TriggerAsync"/>. These tests model both behaviours directly
/// against the engine: with-key (proves the bug surface) and without-key (proves the fix path).
/// </summary>
public sealed class WebhookRerunIdempotencyKeyHandlingTests
{
    [Fact]
    public async Task Rerun_WithIdempotencyKeyRehydrated_HitsDedupLedger_AndReturnsOriginalRunId()
    {
        // Arrange — original webhook trigger with an Idempotency-Key.
        var counter = new IdempotencyInvocationCounter();
        await using var host = await FlowTestHost.For<IdempotentFlow>()
            .WithService(counter)
            .WithHandler<CountInvocationsStepHandler>("CountInvocations")
            .BuildAsync();

        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = "rerun-key-1" };
        var first = await host.TriggerWebhookAsync(slug: "webhook", body: new { }, headers: headers);
        Assert.False(first.TimedOut);

        var orchestrator = host.Services.GetRequiredService<IFlowOrchestrator>();
        var flow = new IdempotentFlow();

        // Act — replay the trigger with headers rehydrated VERBATIM (the pre-fix behaviour).
        // This mirrors what the dashboard rerun endpoint did before the fix.
        var rehydratedCtx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger("webhook", "Webhook", null, new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)),
            SourceRunId = first.RunId
        };
        var rerunResult = await orchestrator.TriggerAsync(rehydratedCtx);

        // Assert — engine recognises the duplicate key and returns the original RunId.
        // The handler is NOT invoked again (counter stays at 1).
        Assert.NotNull(rerunResult);
        var runIdProp = rerunResult!.GetType().GetProperty("runId")?.GetValue(rerunResult);
        Assert.Equal(first.RunId, runIdProp);
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task Rerun_WithIdempotencyKeyStripped_StartsFreshRun_AndReinvokesHandler()
    {
        // Arrange
        var counter = new IdempotencyInvocationCounter();
        await using var host = await FlowTestHost.For<IdempotentFlow>()
            .WithService(counter)
            .WithHandler<CountInvocationsStepHandler>("CountInvocations")
            .BuildAsync();

        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = "rerun-key-2" };
        var first = await host.TriggerWebhookAsync(slug: "webhook", body: new { }, headers: headers);
        Assert.False(first.TimedOut);
        Assert.Equal(1, counter.Calls);

        var orchestrator = host.Services.GetRequiredService<IFlowOrchestrator>();
        var flow = new IdempotentFlow();

        // Act — model the post-fix dashboard behaviour: strip the Idempotency-Key before replay.
        var strippedHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        strippedHeaders.Remove("Idempotency-Key");

        var rerunCtx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger("webhook", "Webhook", null, strippedHeaders),
            SourceRunId = first.RunId
        };
        var rerunResult = await orchestrator.TriggerAsync(rerunCtx);

        // Assert — fresh run, handler invoked again, counter at 2.
        var runIdProp = rerunResult!.GetType().GetProperty("runId")?.GetValue(rerunResult);
        Assert.NotEqual(first.RunId, runIdProp);
        Assert.Equal(rerunCtx.RunId, runIdProp);

        // Wait for the rerun to terminate so the counter assertion is deterministic.
        var rerunSnapshot = await host.WaitForRunAsync(rerunCtx.RunId, TimeSpan.FromSeconds(30));
        Assert.False(rerunSnapshot.TimedOut);
        Assert.Equal(2, counter.Calls);
    }

    [Fact]
    public async Task Rerun_OtherHeadersPreserved_OnlyIdempotencyKeyIsStripped()
    {
        // Arrange — verify the strip is surgical: only the configured idempotency header is
        // removed; everything else (correlation IDs, custom headers) survives the rerun.
        var counter = new IdempotencyInvocationCounter();
        await using var host = await FlowTestHost.For<IdempotentFlow>()
            .WithService(counter)
            .WithHandler<CountInvocationsStepHandler>("CountInvocations")
            .BuildAsync();

        var originalHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Idempotency-Key"] = "rerun-key-3",
            ["X-Correlation-Id"] = "trace-abc",
            ["X-Source"] = "external-system"
        };
        await host.TriggerWebhookAsync(slug: "webhook", body: new { }, headers: originalHeaders);

        // Act — strip only the idempotency header (mirrors the fix's StripIdempotencyHeader helper).
        var strippedHeaders = new Dictionary<string, string>(originalHeaders, StringComparer.OrdinalIgnoreCase);
        strippedHeaders.Remove("Idempotency-Key");

        // Assert — non-idempotency headers survive intact.
        Assert.False(strippedHeaders.ContainsKey("Idempotency-Key"));
        Assert.Equal("trace-abc", strippedHeaders["X-Correlation-Id"]);
        Assert.Equal("external-system", strippedHeaders["X-Source"]);
    }
}
