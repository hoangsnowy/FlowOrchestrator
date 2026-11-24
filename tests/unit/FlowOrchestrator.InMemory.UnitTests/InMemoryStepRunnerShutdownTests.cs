using System.Threading.Channels;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// Shutdown contract of <see cref="InMemoryStepRunnerHostedService"/>: a step already running when the host
/// stops is allowed to return before the service reports itself stopped.
/// </summary>
/// <remarks>
/// The runner used to fire each step on a discarded <see cref="Task.Run(Func{Task})"/> and return from
/// <c>ExecuteAsync</c> as soon as the channel loop saw the stopping token. The host then disposed the
/// services a running step still needed (its DI scope, the store's connections) while that step was writing
/// its final status, releasing its claim or rescheduling a poll.
/// </remarks>
public sealed class InMemoryStepRunnerShutdownTests
{
    [Fact]
    public async Task StopAsync_StepStillRunning_WaitsForTheStepToReturn()
    {
        // Arrange — the step observes the stopping token, then needs a moment to finish its bookkeeping.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = 0;
        var engine = Substitute.For<IFlowOrchestrator>();
        engine.RunStepAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<object?>(RunStepAsync(call.Arg<CancellationToken>())));

        async Task<object?> RunStepAsync(CancellationToken ct)
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // Shutdown observed — the step still has to persist its outcome.
            }

            await release.Task;
            Interlocked.Exchange(ref finished, 1);
            return null;
        }

        var services = new ServiceCollection().AddSingleton(engine).BuildServiceProvider();
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var sut = new InMemoryStepRunnerHostedService(
            channel.Reader,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InMemoryStepRunnerHostedService>.Instance);
        await sut.StartAsync(CancellationToken.None);
        await channel.Writer.WriteAsync(new InMemoryStepEnvelope(
            new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() },
            Substitute.For<IFlowDefinition>(),
            new StepInstance("step1", "DoWork"),
            "env-1"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Act — stop while the step is still running; the step can only return once released.
        var stop = sut.StopAsync(CancellationToken.None);
        await Task.WhenAny(stop, Task.Delay(50));
        var stoppedBeforeStepReturned = stop.IsCompleted;
        release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert — StopAsync did not return ahead of the running step.
        Assert.False(stoppedBeforeStepReturned, "StopAsync returned while a step was still running.");
        Assert.Equal(1, Volatile.Read(ref finished));
    }

    [Fact]
    public async Task StopAsync_NoStepRunning_ReturnsWithoutFaulting()
    {
        // Arrange
        var services = new ServiceCollection().AddSingleton(Substitute.For<IFlowOrchestrator>()).BuildServiceProvider();
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var sut = new InMemoryStepRunnerHostedService(
            channel.Reader,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InMemoryStepRunnerHostedService>.Instance);
        await sut.StartAsync(CancellationToken.None);

        // Act
        await sut.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        Assert.False(sut.ExecuteTask!.IsFaulted, $"ExecuteTask faulted: {sut.ExecuteTask.Exception}");
    }
}
