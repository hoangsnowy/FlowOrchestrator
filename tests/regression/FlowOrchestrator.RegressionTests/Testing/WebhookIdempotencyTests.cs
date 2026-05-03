using FlowOrchestrator.Testing.Tests.Fixtures;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>
/// Regression coverage for the webhook idempotency invariant: triggers carrying the same
/// <c>Idempotency-Key</c> header are de-duplicated through
/// <see cref="FlowOrchestrator.Core.Storage.IFlowRunControlStore.TryRegisterIdempotencyKeyAsync"/>.
/// First call creates a run; second call resolves to the same RunId without re-dispatching.
/// </summary>
public sealed class WebhookIdempotencyTests
{
    [Fact]
    public async Task TwoTriggers_SameIdempotencyKey_ProduceSingleRunWithExactlyOneStepInvocation()
    {
        // Arrange
        var counter = new IdempotencyInvocationCounter();
        await using var host = await FlowTestHost.For<IdempotentFlow>()
            .WithService(counter)
            .WithHandler<CountInvocationsStepHandler>("CountInvocations")
            .BuildAsync();

        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = "key-abc-123" };

        // Act — fire the same webhook twice with identical idempotency key.
        var first = await host.TriggerWebhookAsync(slug: "webhook", body: new { }, headers: headers);
        var second = await host.TriggerWebhookAsync(slug: "webhook", body: new { }, headers: headers);

        // Assert — both calls converge on the same RunId and only one handler invocation occurred.
        Assert.False(first.TimedOut);
        Assert.False(second.TimedOut);
        Assert.Equal(first.RunId, second.RunId);
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task TwoTriggers_DifferentIdempotencyKeys_ProduceDistinctRuns()
    {
        // Arrange
        var counter = new IdempotencyInvocationCounter();
        await using var host = await FlowTestHost.For<IdempotentFlow>()
            .WithService(counter)
            .WithHandler<CountInvocationsStepHandler>("CountInvocations")
            .BuildAsync();

        // Act
        var first = await host.TriggerWebhookAsync(
            slug: "webhook",
            body: new { },
            headers: new Dictionary<string, string> { ["Idempotency-Key"] = "key-1" });
        var second = await host.TriggerWebhookAsync(
            slug: "webhook",
            body: new { },
            headers: new Dictionary<string, string> { ["Idempotency-Key"] = "key-2" });

        // Assert — distinct runs, two handler invocations.
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(2, counter.Calls);
    }
}
