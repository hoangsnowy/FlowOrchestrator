using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.ServiceBus;
using NSubstitute;

namespace FlowOrchestrator.ServiceBus.UnitTests;

/// <summary>
/// Edge-case checks on <see cref="ServiceBusStepDispatcher.BuildMessage"/> — specifically
/// the MessageId construction, which is the duplicate-detection key on the topic. Two
/// reschedules of the same step with identical <see cref="IStepInstance.ScheduledTime"/>
/// produce identical MessageIds, so the second is silently swallowed by SB. We document
/// this contract so behaviour change is caught in code review.
/// </summary>
public class ServiceBusStepDispatcherEdgeCaseTests
{
    private static (IExecutionContext ctx, IFlowDefinition flow, IStepInstance step) Args(DateTimeOffset scheduled)
    {
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        var step = new StepInstance("step", "Type")
        {
            RunId = ctx.RunId,
            ScheduledTime = scheduled,
        };
        return (ctx, flow, step);
    }

    [Fact]
    public void BuildMessage_SameRunStepAndScheduledTime_ProducesIdenticalMessageId()
    {
        // Arrange — two reschedules with identical ScheduledTime simulate the dedup-collision
        // path: the SB topic's duplicate-detection window will treat the second as a dupe.
        var scheduled = new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero);
        var (ctx, flow, step) = Args(scheduled);
        var (ctx2, _, _) = Args(scheduled);
        // Force the same RunId/StepKey/ScheduledTime — same dedup key.
        var step2 = new StepInstance(step.Key, step.Type) { RunId = ctx.RunId, ScheduledTime = scheduled };

        // Act
        var first = ServiceBusStepDispatcher.BuildMessage(ctx, flow, step, scheduledEnqueueAt: null);
        var second = ServiceBusStepDispatcher.BuildMessage(ctx, flow, step2, scheduledEnqueueAt: null);

        // Assert — caller-visible contract. Engine's TryRecordDispatchAsync ledger is the
        // authoritative idempotency layer; SB MessageId is best-effort dedup at the broker.
        Assert.Equal(first.MessageId, second.MessageId);
    }

    [Fact]
    public void BuildMessage_DifferentScheduledTime_ProducesDistinctMessageIds()
    {
        // Arrange — Pending → reschedule path: each retry should bump ScheduledTime,
        // yielding a distinct MessageId so the broker doesn't eat the new dispatch.
        var t1 = new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddSeconds(15);
        var (ctx, flow, _) = Args(t1);
        var stepFirst = new StepInstance("step", "Type") { RunId = ctx.RunId, ScheduledTime = t1 };
        var stepSecond = new StepInstance("step", "Type") { RunId = ctx.RunId, ScheduledTime = t2 };

        // Act
        var first = ServiceBusStepDispatcher.BuildMessage(ctx, flow, stepFirst, scheduledEnqueueAt: null);
        var second = ServiceBusStepDispatcher.BuildMessage(ctx, flow, stepSecond, scheduledEnqueueAt: null);

        // Assert
        Assert.NotEqual(first.MessageId, second.MessageId);
    }

    [Fact]
    public void BuildMessage_DefaultScheduledTime_ProducesStableId()
    {
        // Arrange — defensive: a step with default ScheduledTime (DateTimeOffset.MinValue) must
        // not throw when computing UtcTicks, and must yield a deterministic id so the engine
        // can match dispatch attempts.
        var (ctx, flow, _) = Args(default);
        var step = new StepInstance("k", "T") { RunId = ctx.RunId, ScheduledTime = default };

        // Act
        var msg = ServiceBusStepDispatcher.BuildMessage(ctx, flow, step, scheduledEnqueueAt: null);

        // Assert
        Assert.Contains(ctx.RunId.ToString(), msg.MessageId);
        Assert.EndsWith(":0", msg.MessageId); // UtcTicks of MinValue is 0
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
