using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.ServiceBus;
using NSubstitute;

namespace FlowOrchestrator.ServiceBus.UnitTests;

/// <summary>
/// Verifies the message-shaping logic of <see cref="ServiceBusStepDispatcher"/>.
/// We can't mock <c>ServiceBusSender</c> directly (sealed by SDK), but the static
/// <c>BuildMessage</c> helper exposes the message we'd ship — which is the surface
/// that actually matters for the SB filter / dedup contract.
/// </summary>
public class ServiceBusStepDispatcherTests
{
    private static (IExecutionContext ctx, IFlowDefinition flow, IStepInstance step) MakeArgs()
    {
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext
        {
            RunId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            PrincipalId = "user@example.com",
            TriggerHeaders = new Dictionary<string, string> { ["X-Correlation-Id"] = "abc" },
        };
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.Parse("22222222-2222-2222-2222-222222222222"));

        var step = new StepInstance("step1", "MyStep")
        {
            RunId = ctx.RunId,
            ScheduledTime = new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero),
        };
        step.Inputs["foo"] = "bar";
        return (ctx, flow, step);
    }

    [Fact]
    public void BuildMessage_PopulatesApplicationProperties()
    {
        // Arrange
        var (ctx, flow, step) = MakeArgs();

        // Act
        var msg = ServiceBusStepDispatcher.BuildMessage(ctx, flow, step, scheduledEnqueueAt: null);

        // Assert
        Assert.Equal(flow.Id.ToString(), msg.ApplicationProperties["FlowId"]);
        Assert.Equal(ctx.RunId.ToString(), msg.ApplicationProperties["RunId"]);
        Assert.Equal(step.Key, msg.ApplicationProperties["StepKey"]);
        Assert.Equal(step.Key, msg.Subject);
        Assert.Equal("application/json", msg.ContentType);
    }

    [Fact]
    public void BuildMessage_MessageIdIncludesRunStepAndScheduledTime()
    {
        // Arrange
        var (ctx, flow, step) = MakeArgs();

        // Act
        var msg = ServiceBusStepDispatcher.BuildMessage(ctx, flow, step, scheduledEnqueueAt: null);

        // Assert
        var expected = $"{ctx.RunId}:{step.Key}:{step.ScheduledTime.UtcTicks}";
        Assert.Equal(expected, msg.MessageId);
    }

    [Fact]
    public void BuildMessage_SchedulingSetsScheduledEnqueueTime()
    {
        // Arrange
        var (ctx, flow, step) = MakeArgs();
        var when = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Act
        var msg = ServiceBusStepDispatcher.BuildMessage(ctx, flow, step, scheduledEnqueueAt: when);

        // Assert
        Assert.Equal(when, msg.ScheduledEnqueueTime);
    }

    [Fact]
    public void BuildMessage_BodyDeserialisesBackToEquivalentEnvelope()
    {
        // Arrange
        var (ctx, flow, step) = MakeArgs();

        // Act
        var msg = ServiceBusStepDispatcher.BuildMessage(ctx, flow, step, scheduledEnqueueAt: null);
        var roundTrip = JsonSerializer.Deserialize<StepEnvelope>(msg.Body.ToArray());

        // Assert
        Assert.NotNull(roundTrip);
        Assert.Equal(flow.Id, roundTrip!.FlowId);
        Assert.Equal(ctx.RunId, roundTrip.RunId);
        Assert.Equal(step.Key, roundTrip.StepKey);
        Assert.Equal(step.Type, roundTrip.StepType);
        Assert.Equal(step.ScheduledTime, roundTrip.ScheduledTime);
        Assert.NotNull(roundTrip.Inputs);
        Assert.True(roundTrip.Inputs!.ContainsKey("foo"));
    }

    [Fact]
    public void BuildMessage_NotScheduled_LeavesScheduledEnqueueTimeAtDefault()
    {
        // Arrange
        var (ctx, flow, step) = MakeArgs();

        // Act
        var msg = ServiceBusStepDispatcher.BuildMessage(ctx, flow, step, scheduledEnqueueAt: null);

        // Assert
        // Default is DateTimeOffset.MinValue which the SDK interprets as "send immediately".
        Assert.Equal(default, msg.ScheduledEnqueueTime);
    }
}
