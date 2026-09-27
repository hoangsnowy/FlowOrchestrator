using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.ServiceBus;
using NSubstitute;

namespace FlowOrchestrator.ServiceBus.UnitTests;

/// <summary>
/// Edge-case checks on <see cref="ServiceBusStepDispatcher.BuildMessage"/> — specifically the
/// MessageId construction, which is the duplicate-detection key on the step topic.
/// </summary>
/// <remarks>
/// The topic drops any message whose id it has seen in the last 10 minutes, silently: no error, no
/// dead-letter. Up to v1.32 the id was derived from <c>IStepInstance.ScheduledTime</c>, so two
/// dispatches of the same step sharing a tick lost the second one and stranded the step until the
/// run timeout sweep (issue #186). The id is now unique per dispatch by construction; these tests pin
/// that contract so a regression back to a derived id is caught in review.
/// </remarks>
public class ServiceBusStepDispatcherEdgeCaseTests
{
    private static (IExecutionContext ctx, IFlowDefinition flow, IStepInstance step) Args(DateTimeOffset scheduled, string stepKey = "step")
    {
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        var step = new StepInstance(stepKey, "Type")
        {
            RunId = ctx.RunId,
            ScheduledTime = scheduled,
        };
        return (ctx, flow, step);
    }

    [Fact]
    public void BuildMessage_SameRunStepAndPinnedScheduledTime_ProducesDistinctMessageIds()
    {
        // Arrange — the #186 collision: two reschedules of one step with an identical ScheduledTime
        // inside the 10-minute dedup window. Before the fix the second was silently dropped.
        var scheduled = new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero);
        var (ctx, flow, step) = Args(scheduled);
        var step2 = new StepInstance(step.Key, step.Type) { RunId = ctx.RunId, ScheduledTime = scheduled };

        // Act
        var first = ServiceBusStepDispatcher.BuildMessage(ctx, flow, step, scheduledEnqueueAt: null);
        var second = ServiceBusStepDispatcher.BuildMessage(ctx, flow, step2, scheduledEnqueueAt: null);

        // Assert — the ledger and claim own idempotency; the broker must never eat a real dispatch.
        Assert.NotEqual(first.MessageId, second.MessageId);
    }

    [Fact]
    public void BuildMessage_SameStepInstanceDispatchedTwice_ProducesDistinctMessageIds()
    {
        // Arrange — the engine reuses one IStepInstance across a reschedule, so the id must not be a
        // function of the instance either.
        var (ctx, flow, step) = Args(new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero));

        // Act
        var ids = Enumerable.Range(0, 100)
            .Select(_ => ServiceBusStepDispatcher.BuildMessage(ctx, flow, step, scheduledEnqueueAt: null).MessageId)
            .ToHashSet();

        // Assert
        Assert.Equal(100, ids.Count);
    }

    [Fact]
    public void BuildMessage_DefaultScheduledTime_StillProducesAUsableId()
    {
        // Arrange — defensive: a step with default ScheduledTime (DateTimeOffset.MinValue) used to
        // yield the fixed suffix ":0", i.e. the same id for every such dispatch of the step.
        var (ctx, flow, step) = Args(default, stepKey: "k");

        // Act
        var msg = ServiceBusStepDispatcher.BuildMessage(ctx, flow, step, scheduledEnqueueAt: null);

        // Assert
        Assert.StartsWith($"{ctx.RunId}:k:", msg.MessageId);
        Assert.False(msg.MessageId.EndsWith(":0", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildMessage_LongStepKey_StaysWithinTheBrokerMessageIdLimit()
    {
        // Arrange — nested ForEach keys ("outer.12.inner.3.step") can get long; Service Bus rejects a
        // MessageId over 128 characters, which would fail the send outright.
        var longKey = string.Join('.', Enumerable.Range(0, 20).Select(i => $"loop_{i}"));
        var (ctx, flow, step) = Args(DateTimeOffset.UtcNow, longKey);

        // Act
        var msg = ServiceBusStepDispatcher.BuildMessage(ctx, flow, step, scheduledEnqueueAt: null);

        // Assert — the key is dropped from the id but still travels as Subject + application property.
        Assert.True(msg.MessageId.Length <= ServiceBusStepDispatcher.MaxMessageIdLength, msg.MessageId);
        Assert.StartsWith(ctx.RunId.ToString(), msg.MessageId);
        Assert.Equal(longKey, msg.Subject);
        Assert.Equal(longKey, msg.ApplicationProperties["StepKey"]);
    }

    [Theory]
    [InlineData(57, true)]
    [InlineData(58, true)]  // 36 (runId) + 1 + 58 + 1 + 32 (nonce) = exactly 128
    [InlineData(59, false)] // one over: falls back to {runId}:{nonce}
    public void BuildMessageId_keeps_the_step_key_exactly_up_to_the_128_character_limit(int keyLength, bool keyKept)
    {
        // Arrange
        var runId = Guid.NewGuid();
        var key = new string('k', keyLength);

        // Act
        var id = ServiceBusStepDispatcher.BuildMessageId(runId, key);

        // Assert
        Assert.True(id.Length <= ServiceBusStepDispatcher.MaxMessageIdLength, $"{id.Length}: {id}");
        if (keyKept)
        {
            Assert.StartsWith($"{runId}:{key}:", id);
            Assert.Equal(36 + 1 + keyLength + 1 + 32, id.Length);
        }
        else
        {
            Assert.DoesNotContain(key, id);
            Assert.Equal(36 + 1 + 32, id.Length);
        }
    }

    [Fact]
    public void BuildMessageId_fallback_ids_are_still_unique_per_dispatch()
    {
        // Arrange — the fallback drops the key, so uniqueness rests on the nonce alone.
        var runId = Guid.NewGuid();
        var key = new string('k', 200);

        // Act
        var ids = Enumerable.Range(0, 100).Select(_ => ServiceBusStepDispatcher.BuildMessageId(runId, key)).ToHashSet();

        // Assert
        Assert.Equal(100, ids.Count);
    }

    [Fact]
    public void BuildMessage_DeliversApplicationPropertiesAsStrings()
    {
        // Arrange — SB SQL filters require the ApplicationProperty to be a primitive type.
        // If a refactor accidentally stored the Guid directly, per-flow subscription filters
        // would silently fail to match. Encoding as string is part of the public contract.
        var (ctx, flow, step) = Args(new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero));

        // Act
        var msg = ServiceBusStepDispatcher.BuildMessage(ctx, flow, step, scheduledEnqueueAt: null);

        // Assert
        Assert.IsType<string>(msg.ApplicationProperties["FlowId"]);
        Assert.IsType<string>(msg.ApplicationProperties["RunId"]);
        Assert.IsType<string>(msg.ApplicationProperties["StepKey"]);
    }
}
