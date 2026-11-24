using System.Threading.Channels;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.InMemory;
using NSubstitute;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// <see cref="IStepDispatcher.ScheduleStepAsync"/> contract parity for <see cref="InMemoryStepDispatcher"/>:
/// a delay that is already in the past must behave like an immediate enqueue, as it does on the other two
/// runtimes.
/// </summary>
/// <remarks>
/// Hangfire turns <c>Schedule(job, delay)</c> into a <c>ScheduledState</c> at <c>UtcNow + delay</c>; a past
/// instant is enqueued on the next scheduler poll. Service Bus delivers a message whose
/// <c>ScheduledEnqueueTime</c> is in the past straight away. The engine hands a handler's
/// <c>DelayNextStep</c> (Pending path) or a <c>StepDispatchHint</c> child delay to the dispatcher unclamped,
/// so a custom handler computing <c>deadline - now</c> can produce a negative value. The in-memory
/// dispatcher feeds it to <see cref="Task.Delay(TimeSpan, CancellationToken)"/> inside a discarded
/// fire-and-forget task: <c>-1 ms</c> (<see cref="Timeout.InfiniteTimeSpan"/>) waits forever, any other
/// negative value throws <see cref="ArgumentOutOfRangeException"/> into an unobserved task. Either way
/// <c>ScheduleStepAsync</c> has already returned a job id, the dispatch-ledger row stays, and the step is
/// never delivered — the run sits Running with a Pending step until restart.
/// </remarks>
public sealed class InMemoryStepDispatcherDelayContractTests
{
    [Theory]
    [InlineData(-1)]      // Timeout.InfiniteTimeSpan — Task.Delay waits for ever
    [InlineData(-5000)]   // any other negative value — Task.Delay throws into a discarded task
    public async Task ScheduleStepAsync_DelayAlreadyInThePast_DeliversTheStepImmediately(int delayMs)
    {
        // Arrange
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var sut = new InMemoryStepDispatcher(channel.Writer);
        var ctx = Substitute.For<IExecutionContext>();
        var flow = Substitute.For<IFlowDefinition>();
        var step = new StepInstance("poll", "Poll") { RunId = Guid.NewGuid() };

        // Act
        var jobId = await sut.ScheduleStepAsync(ctx, flow, step, TimeSpan.FromMilliseconds(delayMs));

        // Assert — a past delay must reach the runner like an immediate enqueue does.
        Assert.NotNull(jobId);
        var read = channel.Reader.ReadAsync().AsTask();
        var completed = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(completed == read,
            $"ScheduleStepAsync(delay: {delayMs} ms) returned job id '{jobId}' but never delivered the step to the channel.");
        Assert.Equal(jobId, (await read).EnvelopeId);
    }
}
