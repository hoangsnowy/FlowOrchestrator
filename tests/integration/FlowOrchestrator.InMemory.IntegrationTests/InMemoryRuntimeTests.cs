using System.Threading.Channels;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// Unit and integration tests for the in-memory step-execution runtime
/// (<see cref="InMemoryStepDispatcher"/> and <see cref="InMemoryStepRunnerHostedService"/>).
/// </summary>
public sealed class InMemoryRuntimeTests
{
    private static IExecutionContext MakeContext(Guid runId) =>
        new Core.Execution.ExecutionContext { RunId = runId };

    private static IFlowDefinition MakeFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest());
        return flow;
    }

    private static IStepInstance MakeStep(string key = "step1") =>
        new StepInstance(key, "TestStep");

    [Fact]
    public async Task EnqueueStepAsync_WritesEnvelopeToChannel()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var dispatcher = new InMemoryStepDispatcher(channel.Writer);
        var ctx = MakeContext(Guid.NewGuid());
        var flow = MakeFlow();
        var step = MakeStep();

        // Act
        var jobId = await dispatcher.EnqueueStepAsync(ctx, flow, step);

        // Assert
        Assert.True(channel.Reader.TryRead(out var envelope));
        Assert.NotNull(envelope);
        Assert.Equal(ctx.RunId, envelope!.Context.RunId);
        Assert.Equal(step.Key, envelope.Step.Key);
        Assert.False(string.IsNullOrEmpty(jobId));
    }

    [Fact]
    public async Task EnqueueStepAsync_CalledTwice_WritesTwoEnvelopes()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var dispatcher = new InMemoryStepDispatcher(channel.Writer);
        var ctx = MakeContext(Guid.NewGuid());
        var flow = MakeFlow();

        // Act
        await dispatcher.EnqueueStepAsync(ctx, flow, MakeStep("a"));
        await dispatcher.EnqueueStepAsync(ctx, flow, MakeStep("b"));

        // Assert
        Assert.Equal(2, channel.Reader.Count);
    }

    [Fact]
    public async Task ScheduleStepAsync_WithZeroDelay_WritesEnvelopePromptly()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var dispatcher = new InMemoryStepDispatcher(channel.Writer);
        var ctx = MakeContext(Guid.NewGuid());
        var flow = MakeFlow();
        var step = MakeStep();

        // Act
        var jobId = await dispatcher.ScheduleStepAsync(ctx, flow, step, TimeSpan.Zero);
        // Wait on a logical signal — channel write completes synchronously on
        // unbounded channels, but WaitToReadAsync still gives us a hard guarantee
        // without depending on a wall-clock sleep that flakes under CI load.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.True(await channel.Reader.WaitToReadAsync(cts.Token), "Envelope was not written within 30s.");

        // Assert
        Assert.True(channel.Reader.TryRead(out var envelope));
        Assert.Equal(jobId, envelope!.EnvelopeId);
    }

    [Fact]
    public async Task ScheduleStepAsync_ReturnsDistinctIdFromEnqueue()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var dispatcher = new InMemoryStepDispatcher(channel.Writer);
        var ctx = MakeContext(Guid.NewGuid());
        var flow = MakeFlow();

        // Act
        var enqueueId = await dispatcher.EnqueueStepAsync(ctx, flow, MakeStep("a"));
        var scheduleId = await dispatcher.ScheduleStepAsync(ctx, flow, MakeStep("b"), TimeSpan.Zero);

        // Assert
        Assert.NotEqual(scheduleId, enqueueId);
    }

    [Fact]
    public async Task Runner_WhenEnvelopeWritten_CallsEngineRunStepAsync()
    {
        // Arrange — drive the assertion off a logical signal (TaskCompletionSource)
        // rather than `await Task.Delay(N); engine.Received(1)…`, which races under CI load.
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var engine = Substitute.For<IFlowOrchestrator>();
        var firstCallTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.RunStepAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(),
                            Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>())
              .Returns(_ =>
              {
                  firstCallTcs.TrySetResult();
                  return new ValueTask<object?>((object?)null);
              });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(engine);
        services.AddSingleton(channel.Reader);
        services.AddHostedService<InMemoryStepRunnerHostedService>();
        var sp = services.BuildServiceProvider();

        var cts = new CancellationTokenSource();
        var runner = sp.GetServices<IHostedService>().OfType<InMemoryStepRunnerHostedService>().Single();
        var ctx = MakeContext(Guid.NewGuid());
        var flow = MakeFlow();
        var step = MakeStep();

        // Act
        var runTask = runner.StartAsync(cts.Token);
        await channel.Writer.WriteAsync(new InMemoryStepEnvelope(ctx, flow, step, "e1"));
        await firstCallTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();

        // Assert
        await engine.Received(1).RunStepAsync(
            Arg.Is<IExecutionContext>(c => c.RunId == ctx.RunId),
            Arg.Any<IFlowDefinition>(),
            Arg.Is<IStepInstance>(s => s.Key == step.Key),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Runner_EngineThrows_ContinuesProcessingNextEnvelope()
    {
        // Arrange — wait on a logical signal that fires after the second call
        // arrives, instead of a hardcoded Task.Delay sleep.
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var engine = Substitute.For<IFlowOrchestrator>();
        var callCount = 0;
        var secondCallTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.RunStepAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(),
                            Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>())
              .Returns(ci =>
              {
                  var n = Interlocked.Increment(ref callCount);
                  if (n == 1)
                      throw new InvalidOperationException("Simulated step failure");
                  secondCallTcs.TrySetResult();
                  return new ValueTask<object?>((object?)null);
              });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(engine);
        services.AddSingleton(channel.Reader);
        services.AddHostedService<InMemoryStepRunnerHostedService>();
        var sp = services.BuildServiceProvider();

        var cts = new CancellationTokenSource();
        var runner = sp.GetServices<IHostedService>().OfType<InMemoryStepRunnerHostedService>().Single();
        var ctx = MakeContext(Guid.NewGuid());
        var flow = MakeFlow();

        // Act
        await runner.StartAsync(cts.Token);
        await channel.Writer.WriteAsync(new InMemoryStepEnvelope(ctx, flow, MakeStep("failing"), "e1"));
        await channel.Writer.WriteAsync(new InMemoryStepEnvelope(ctx, flow, MakeStep("ok"), "e2"));
        await secondCallTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();

        // Assert
        await engine.Received(2).RunStepAsync(
            Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(),
            Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void UseInMemoryRuntime_RegistersStepDispatcher()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IFlowStore>());
        services.AddFlowOrchestrator(opts =>
        {
            opts.UseInMemory();
            opts.UseInMemoryRuntime();
        });

        // Act
        using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<IStepDispatcher>();

        // Assert
        Assert.IsType<InMemoryStepDispatcher>(dispatcher);
    }

    [Fact]
    public void UseInMemoryRuntime_RegistersPeriodicTimerRecurringDispatcher()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IFlowStore>());
        services.AddFlowOrchestrator(opts =>
        {
            opts.UseInMemory();
            opts.UseInMemoryRuntime();
        });

        // Act
        using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<IRecurringTriggerDispatcher>();

        // Assert
        Assert.IsType<PeriodicTimerRecurringTriggerDispatcher>(dispatcher);
    }

    [Fact]
    public void UseInMemoryRuntime_RegistersPeriodicTimerRecurringInspector()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IFlowStore>());
        services.AddFlowOrchestrator(opts =>
        {
            opts.UseInMemory();
            opts.UseInMemoryRuntime();
        });

        // Act
        using var sp = services.BuildServiceProvider();
        var inspector = sp.GetRequiredService<IRecurringTriggerInspector>();

        // Assert
        Assert.IsType<PeriodicTimerRecurringTriggerDispatcher>(inspector);
    }

    [Fact]
    public void UseInMemoryRuntime_AllRecurringInterfacesShareSingleInstance()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IFlowStore>());
        services.AddFlowOrchestrator(opts =>
        {
            opts.UseInMemory();
            opts.UseInMemoryRuntime();
        });

        // Act
        using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<IRecurringTriggerDispatcher>();
        var inspector = sp.GetRequiredService<IRecurringTriggerInspector>();
        var sync = sp.GetRequiredService<IRecurringTriggerSync>();

        // Assert — all three resolve to the same PeriodicTimer instance.
        Assert.Same(dispatcher, inspector);
        Assert.Same(dispatcher, sync);
    }
}
