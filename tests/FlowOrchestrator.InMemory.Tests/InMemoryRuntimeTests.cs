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
        await Task.Delay(100);

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
        // Arrange
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var engine = Substitute.For<IFlowOrchestrator>();
        engine.RunStepAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(),
                            Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>())
              .Returns(new ValueTask<object?>((object?)null));

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
        await Task.Delay(200);
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
        // Arrange
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var engine = Substitute.For<IFlowOrchestrator>();
        var callCount = 0;
        engine.RunStepAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(),
                            Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>())
              .Returns(ci =>
              {
                  callCount++;
                  if (callCount == 1)
                      throw new InvalidOperationException("Simulated step failure");
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
        await Task.Delay(300);
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
    public void UseInMemoryRuntime_RegistersNullRecurringDispatcher()
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
        Assert.IsType<NullRecurringTriggerDispatcher>(dispatcher);
    }

    [Fact]
    public void UseInMemoryRuntime_RegistersNullRecurringInspector()
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
        Assert.IsType<NullRecurringTriggerInspector>(inspector);
    }

    [Fact]
    public async Task NullRecurringDispatcher_AllOperationsAreNoOps()
    {
        // Arrange
        var dispatcher = new NullRecurringTriggerDispatcher();

        // Act
        var ex = await Record.ExceptionAsync(async () =>
        {
            dispatcher.RegisterOrUpdate("job1", Guid.NewGuid(), "schedule", "0 * * * *");
            dispatcher.Remove("job1");
            dispatcher.TriggerOnce("job1");
            await dispatcher.EnqueueTriggerAsync(Guid.NewGuid(), "schedule");
        });

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public async Task NullRecurringInspector_ReturnsEmptyList()
    {
        // Arrange
        var inspector = new NullRecurringTriggerInspector();

        // Act
        var jobs = await inspector.GetJobsAsync();

        // Assert
        Assert.Empty(jobs);
    }
}
