using System.Threading.Channels;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.InMemory;
using NSubstitute;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// Behaviour tests for <see cref="InMemoryStepDispatcher"/> — particularly the deferred
/// <c>ScheduleStepAsync</c> path, which v1.26.1 was silently dropping when the caller
/// passed an HTTP-scoped cancellation token that completed before the delay elapsed.
/// </summary>
public sealed class InMemoryStepDispatcherTests
{
    private static (InMemoryStepDispatcher dispatcher, Channel<InMemoryStepEnvelope> channel) CreateSut()
    {
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        return (new InMemoryStepDispatcher(channel.Writer), channel);
    }

    private static (IExecutionContext ctx, IFlowDefinition flow, IStepInstance step) CreateFixtures()
    {
        var ctx = Substitute.For<IExecutionContext>();
        var flow = Substitute.For<IFlowDefinition>();
        var step = Substitute.For<IStepInstance>();
        return (ctx, flow, step);
    }

    [Fact]
    public async Task EnqueueStepAsync_writes_envelope_immediately()
    {
        // Arrange
        var (sut, channel) = CreateSut();
        var (ctx, flow, step) = CreateFixtures();

        // Act
        var jobId = await sut.EnqueueStepAsync(ctx, flow, step);

        // Assert
        Assert.NotNull(jobId);
        var envelope = await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(jobId, envelope.EnvelopeId);
    }

    [Fact]
    public async Task ScheduleStepAsync_writes_envelope_after_delay_elapses()
    {
        // Arrange
        var (sut, channel) = CreateSut();
        var (ctx, flow, step) = CreateFixtures();

        // Act
        var jobId = await sut.ScheduleStepAsync(ctx, flow, step, TimeSpan.FromMilliseconds(50));

        // Assert
        Assert.NotNull(jobId);
        var envelope = await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(jobId, envelope.EnvelopeId);
    }

    [Fact]
    public async Task ScheduleStepAsync_still_writes_envelope_when_caller_token_is_already_cancelled()
    {
        // This is the v1.26.1 regression: FlowSignalDispatcher calls ScheduleStepAsync
        // with the HTTP request's RequestAborted token, which fires the moment the
        // response flushes. The previous implementation chained that token onto
        // Task.Delay + WriteAsync, so the deferred enqueue was silently cancelled
        // and the parked WaitForSignal step never resumed on the InMemory runtime.
        // The dispatcher must ignore the caller's cancellation for the deferred work.

        // Arrange
        var (sut, channel) = CreateSut();
        var (ctx, flow, step) = CreateFixtures();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // simulate the HTTP request closing before the delay elapses

        // Act
        var jobId = await sut.ScheduleStepAsync(ctx, flow, step, TimeSpan.FromMilliseconds(50), cts.Token);

        // Assert
        Assert.NotNull(jobId);
        var envelope = await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(jobId, envelope.EnvelopeId);
    }

    [Fact]
    public async Task ScheduleStepAsync_swallows_ChannelClosedException_on_host_shutdown()
    {
        // Arrange
        var (sut, channel) = CreateSut();
        var (ctx, flow, step) = CreateFixtures();
        channel.Writer.Complete(); // simulate host shutdown closing the channel

        // Act / Assert — must not throw even though the deferred WriteAsync will fail
        var jobId = await sut.ScheduleStepAsync(ctx, flow, step, TimeSpan.FromMilliseconds(20));
        Assert.NotNull(jobId);

        // Give the background task a moment to attempt the write and catch the exception.
        await Task.Delay(100);
    }
}
