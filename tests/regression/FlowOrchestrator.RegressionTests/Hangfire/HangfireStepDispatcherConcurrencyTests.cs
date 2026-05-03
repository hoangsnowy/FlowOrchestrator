using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Hangfire;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Hangfire.Tests;

/// <summary>
/// Concurrency stress for <see cref="HangfireStepDispatcher"/>: when 64 parallel callers
/// invoke <see cref="HangfireStepDispatcher.EnqueueStepAsync"/>, every call must reach
/// the underlying <see cref="IBackgroundJobClient"/>. The dispatcher itself holds no
/// per-step state, so this guards against accidental synchronization regressions
/// (locks, shared buffers, etc.) being introduced into the hot path.
/// </summary>
public sealed class HangfireStepDispatcherConcurrencyTests
{
    [Fact]
    public async Task EnqueueStepAsync_64ParallelCalls_AllReachBackgroundJobClient()
    {
        // Arrange
        const int parallelism = 64;
        var calls = 0;
        var client = Substitute.For<IBackgroundJobClient>();
        client
            .Create(Arg.Any<Job>(), Arg.Any<IState>())
            .Returns(_ => Interlocked.Increment(ref calls).ToString());

        var dispatcher = new HangfireStepDispatcher(client);

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());

        var ctx = new CoreExecutionContext { RunId = Guid.NewGuid() };
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act — every replica enqueues a distinct step; verifies no shared state corrupts the call site.
        var tasks = Enumerable.Range(0, parallelism)
            .Select(async i =>
            {
                await startGate.Task;
                var step = new StepInstance($"step-{i}", "Work") { RunId = ctx.RunId };
                return await dispatcher.EnqueueStepAsync(ctx, flow, step, CancellationToken.None);
            })
            .ToArray();

        startGate.SetResult();
        var jobIds = await Task.WhenAll(tasks);

        // Assert — every caller received a job ID and the underlying client saw exactly N invocations.
        Assert.All(jobIds, id => Assert.False(string.IsNullOrEmpty(id)));
        Assert.Equal(parallelism, calls);
        Assert.Equal(parallelism, jobIds.Distinct().Count());
        client.Received(parallelism).Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    [Fact]
    public async Task ScheduleStepAsync_64ParallelCalls_AllUseScheduledStateWithDelay()
    {
        // Arrange
        const int parallelism = 64;
        var capturedStates = new System.Collections.Concurrent.ConcurrentBag<IState>();
        var client = Substitute.For<IBackgroundJobClient>();
        client
            .Create(Arg.Any<Job>(), Arg.Any<IState>())
            .Returns(callInfo =>
            {
                capturedStates.Add(callInfo.Arg<IState>());
                return Guid.NewGuid().ToString();
            });

        var dispatcher = new HangfireStepDispatcher(client);

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());

        var ctx = new CoreExecutionContext { RunId = Guid.NewGuid() };
        var delay = TimeSpan.FromSeconds(15);
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var tasks = Enumerable.Range(0, parallelism)
            .Select(async i =>
            {
                await startGate.Task;
                var step = new StepInstance($"step-{i}", "Work") { RunId = ctx.RunId };
                return await dispatcher.ScheduleStepAsync(ctx, flow, step, delay, CancellationToken.None);
            })
            .ToArray();

        startGate.SetResult();
        await Task.WhenAll(tasks);

        // Assert — every Create call used the ScheduledState (Hangfire's name for Schedule()).
        Assert.Equal(parallelism, capturedStates.Count);
        Assert.All(capturedStates, state => Assert.Equal("Scheduled", state.Name));
    }
}
