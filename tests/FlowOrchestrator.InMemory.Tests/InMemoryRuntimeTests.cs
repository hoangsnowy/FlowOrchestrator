using System.Threading.Channels;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using FluentAssertions;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// Unit and integration tests for the in-memory step-execution runtime
/// (<see cref="InMemoryStepDispatcher"/> and <see cref="InMemoryStepRunnerHostedService"/>).
/// </summary>
public sealed class InMemoryRuntimeTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

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

    // ── Dispatcher tests ─────────────────────────────────────────────────

    [Fact]
    public async Task EnqueueStepAsync_WritesEnvelopeToChannel()
    {
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var dispatcher = new InMemoryStepDispatcher(channel.Writer);
        var ctx = MakeContext(Guid.NewGuid());
        var flow = MakeFlow();
        var step = MakeStep();

        var jobId = await dispatcher.EnqueueStepAsync(ctx, flow, step);

        channel.Reader.TryRead(out var envelope).Should().BeTrue("one item should be in the channel");
        envelope.Should().NotBeNull();
        envelope!.Context.RunId.Should().Be(ctx.RunId);
        envelope.Step.Key.Should().Be(step.Key);
        jobId.Should().NotBeNullOrEmpty("dispatcher returns a non-null opaque id");
    }

    [Fact]
    public async Task EnqueueStepAsync_CalledTwice_WritesTwoEnvelopes()
    {
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var dispatcher = new InMemoryStepDispatcher(channel.Writer);
        var ctx = MakeContext(Guid.NewGuid());
        var flow = MakeFlow();

        await dispatcher.EnqueueStepAsync(ctx, flow, MakeStep("a"));
        await dispatcher.EnqueueStepAsync(ctx, flow, MakeStep("b"));

        channel.Reader.Count.Should().Be(2);
    }

    [Fact]
    public async Task ScheduleStepAsync_WithZeroDelay_WritesEnvelopePromptly()
    {
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var dispatcher = new InMemoryStepDispatcher(channel.Writer);
        var ctx = MakeContext(Guid.NewGuid());
        var flow = MakeFlow();
        var step = MakeStep();

        var jobId = await dispatcher.ScheduleStepAsync(ctx, flow, step, TimeSpan.Zero);

        // Allow the fire-and-forget task a moment to complete.
        await Task.Delay(100);

        channel.Reader.TryRead(out var envelope).Should().BeTrue();
        envelope!.EnvelopeId.Should().Be(jobId);
    }

    [Fact]
    public async Task ScheduleStepAsync_ReturnsDistinctIdFromEnqueue()
    {
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var dispatcher = new InMemoryStepDispatcher(channel.Writer);
        var ctx = MakeContext(Guid.NewGuid());
        var flow = MakeFlow();

        var enqueueId = await dispatcher.EnqueueStepAsync(ctx, flow, MakeStep("a"));
        var scheduleId = await dispatcher.ScheduleStepAsync(ctx, flow, MakeStep("b"), TimeSpan.Zero);

        enqueueId.Should().NotBe(scheduleId, "each dispatch produces a unique id");
    }

    // ── Runner tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task Runner_WhenEnvelopeWritten_CallsEngineRunStepAsync()
    {
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
        var runTask = runner.StartAsync(cts.Token);

        var ctx = MakeContext(Guid.NewGuid());
        var flow = MakeFlow();
        var step = MakeStep();
        await channel.Writer.WriteAsync(new InMemoryStepEnvelope(ctx, flow, step, "e1"));

        // Give the runner a moment to process.
        await Task.Delay(200);
        cts.Cancel();

        await engine.Received(1).RunStepAsync(
            Arg.Is<IExecutionContext>(c => c.RunId == ctx.RunId),
            Arg.Any<IFlowDefinition>(),
            Arg.Is<IStepInstance>(s => s.Key == step.Key),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Runner_EngineThrows_ContinuesProcessingNextEnvelope()
    {
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
        await runner.StartAsync(cts.Token);

        var ctx = MakeContext(Guid.NewGuid());
        var flow = MakeFlow();
        await channel.Writer.WriteAsync(new InMemoryStepEnvelope(ctx, flow, MakeStep("failing"), "e1"));
        await channel.Writer.WriteAsync(new InMemoryStepEnvelope(ctx, flow, MakeStep("ok"), "e2"));

        await Task.Delay(300);
        cts.Cancel();

        // Both envelopes were attempted — runner survived the first failure.
        await engine.Received(2).RunStepAsync(
            Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(),
            Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>());
    }

    // ── UseInMemoryRuntime() DI integration ──────────────────────────────

    [Fact]
    public void UseInMemoryRuntime_RegistersStepDispatcher()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Minimal stub so AddFlowOrchestrator's IFlowStore validation passes.
        services.AddSingleton(Substitute.For<IFlowStore>());

        services.AddFlowOrchestrator(opts =>
        {
            opts.UseInMemory();
            opts.UseInMemoryRuntime();
        });

        using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<IStepDispatcher>();
        dispatcher.Should().BeOfType<InMemoryStepDispatcher>();
    }

    [Fact]
    public void UseInMemoryRuntime_RegistersNullRecurringDispatcher()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IFlowStore>());

        services.AddFlowOrchestrator(opts =>
        {
            opts.UseInMemory();
            opts.UseInMemoryRuntime();
        });

        using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<IRecurringTriggerDispatcher>();
        dispatcher.Should().BeOfType<NullRecurringTriggerDispatcher>();
    }

    [Fact]
    public void UseInMemoryRuntime_RegistersNullRecurringInspector()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IFlowStore>());

        services.AddFlowOrchestrator(opts =>
        {
            opts.UseInMemory();
            opts.UseInMemoryRuntime();
        });

        using var sp = services.BuildServiceProvider();
        var inspector = sp.GetRequiredService<IRecurringTriggerInspector>();
        inspector.Should().BeOfType<NullRecurringTriggerInspector>();
    }

    [Fact]
    public async Task NullRecurringDispatcher_AllOperationsAreNoOps()
    {
        var dispatcher = new NullRecurringTriggerDispatcher();

        // None of these should throw.
        dispatcher.RegisterOrUpdate("job1", Guid.NewGuid(), "schedule", "0 * * * *");
        dispatcher.Remove("job1");
        dispatcher.TriggerOnce("job1");
        await dispatcher.EnqueueTriggerAsync(Guid.NewGuid(), "schedule");
    }

    [Fact]
    public async Task NullRecurringInspector_ReturnsEmptyList()
    {
        var inspector = new NullRecurringTriggerInspector();
        var jobs = await inspector.GetJobsAsync();
        jobs.Should().BeEmpty();
    }
}
