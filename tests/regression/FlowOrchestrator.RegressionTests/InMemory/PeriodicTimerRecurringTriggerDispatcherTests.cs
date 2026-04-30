using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// Unit tests for <see cref="PeriodicTimerRecurringTriggerDispatcher"/> covering invariant #8
/// (PeriodicTimer cron) plus pause/resume, override precedence, and exception isolation.
/// </summary>
public sealed class PeriodicTimerRecurringTriggerDispatcherTests
{
    private static (PeriodicTimerRecurringTriggerDispatcher Dispatcher, IFlowOrchestrator Orchestrator, IServiceProvider Sp) CreateSut(
        IFlowRepository? repo = null,
        IFlowScheduleStateStore? scheduleStateStore = null,
        FlowSchedulerOptions? options = null)
    {
        var orchestrator = Substitute.For<IFlowOrchestrator>();
        orchestrator
            .TriggerByScheduleAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<object?>((object?)null));

        var services = new ServiceCollection();
        services.AddSingleton(orchestrator);
        var sp = services.BuildServiceProvider();

        var dispatcher = new PeriodicTimerRecurringTriggerDispatcher(
            sp,
            repo ?? Substitute.For<IFlowRepository>(),
            scheduleStateStore ?? Substitute.For<IFlowScheduleStateStore>(),
            options ?? new FlowSchedulerOptions(),
            NullLogger<PeriodicTimerRecurringTriggerDispatcher>.Instance);

        return (dispatcher, orchestrator, sp);
    }

    [Fact]
    public void RegisterOrUpdate_AddsJob_WithComputedNextExecution()
    {
        // Arrange
        var (dispatcher, _, _) = CreateSut();
        var flowId = Guid.NewGuid();

        // Act
        dispatcher.RegisterOrUpdate("job1", flowId, "schedule", "* * * * *");

        // Assert
        Assert.True(dispatcher.TryGetJob("job1", out var snap));
        Assert.Equal(flowId, snap.FlowId);
        Assert.Equal("schedule", snap.TriggerKey);
        Assert.Equal("* * * * *", snap.EffectiveCron);
        Assert.True(snap.NextExecution > DateTimeOffset.UtcNow);
        Assert.False(snap.Paused);
    }

    [Fact]
    public void Remove_DeletesRegisteredJob()
    {
        // Arrange
        var (dispatcher, _, _) = CreateSut();
        dispatcher.RegisterOrUpdate("job1", Guid.NewGuid(), "schedule", "* * * * *");

        // Act
        dispatcher.Remove("job1");

        // Assert
        Assert.False(dispatcher.TryGetJob("job1", out _));
    }

    [Fact]
    public async Task FireAsync_InvokesOrchestratorTriggerByScheduleAsync()
    {
        // Arrange
        var (dispatcher, orchestrator, _) = CreateSut();
        var flowId = Guid.NewGuid();
        dispatcher.RegisterOrUpdate("job1", flowId, "schedule", "* * * * *");

        // Act
        await dispatcher.FireForTestAsync("job1");

        // Assert
        await orchestrator.Received(1).TriggerByScheduleAsync(
            flowId, "schedule", "job1", Arg.Any<CancellationToken>());

        Assert.True(dispatcher.TryGetJob("job1", out var snap));
        Assert.Equal("Succeeded", snap.LastJobState);
        Assert.NotNull(snap.LastExecution);
    }

    [Fact]
    public async Task FireAsync_OrchestratorThrows_StateMarkedFailed_LoopSurvives()
    {
        // Arrange
        var orchestrator = Substitute.For<IFlowOrchestrator>();
        orchestrator
            .TriggerByScheduleAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<object?>>(_ => throw new InvalidOperationException("boom"));

        var services = new ServiceCollection();
        services.AddSingleton(orchestrator);
        using var sp = services.BuildServiceProvider();

        var dispatcher = new PeriodicTimerRecurringTriggerDispatcher(
            sp,
            Substitute.For<IFlowRepository>(),
            Substitute.For<IFlowScheduleStateStore>(),
            new FlowSchedulerOptions(),
            NullLogger<PeriodicTimerRecurringTriggerDispatcher>.Instance);

        dispatcher.RegisterOrUpdate("job1", Guid.NewGuid(), "schedule", "* * * * *");

        // Act
        var ex = await Record.ExceptionAsync(() => dispatcher.FireForTestAsync("job1"));

        // Assert
        Assert.Null(ex); // Exception swallowed so timer loop survives.
        Assert.True(dispatcher.TryGetJob("job1", out var snap));
        Assert.Equal("Failed", snap.LastJobState);
    }

    [Fact]
    public async Task FireAsync_OnUnknownJob_DoesNothing()
    {
        // Arrange
        var (dispatcher, orchestrator, _) = CreateSut();

        // Act
        await dispatcher.FireForTestAsync("non-existent");

        // Assert
        await orchestrator.DidNotReceiveWithAnyArgs()
            .TriggerByScheduleAsync(default, default!, default, default);
    }

    [Fact]
    public async Task GetJobsAsync_ReturnsRegisteredJobsAsRecurringTriggerInfo()
    {
        // Arrange
        var (dispatcher, _, _) = CreateSut();
        var flowId = Guid.NewGuid();
        dispatcher.RegisterOrUpdate("flow-job-1", flowId, "schedule", "0 * * * *");
        dispatcher.RegisterOrUpdate("flow-job-2", flowId, "secondary", "*/5 * * * *");

        // Act
        var jobs = await dispatcher.GetJobsAsync();

        // Assert
        Assert.Equal(2, jobs.Count);
        Assert.Contains(jobs, j => j.Id == "flow-job-1" && j.Cron == "0 * * * *");
        Assert.Contains(jobs, j => j.Id == "flow-job-2" && j.Cron == "*/5 * * * *");
    }

    [Fact]
    public void SyncTriggers_RegistersCronJobs_ForCronTriggersInFlow()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        var manifest = new FlowManifest
        {
            Triggers =
            {
                ["schedule"] = new TriggerMetadata
                {
                    Type = TriggerType.Cron,
                    Inputs = new Dictionary<string, object?> { ["cronExpression"] = "0 * * * *" }
                }
            }
        };
        flow.Manifest.Returns(manifest);

        var repo = Substitute.For<IFlowRepository>();
        repo.GetAllFlowsAsync()
            .Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new[] { flow }));

        var (dispatcher, _, _) = CreateSut(repo: repo);

        // Act
        dispatcher.SyncTriggers(flowId, isEnabled: true);

        // Assert
        Assert.True(dispatcher.TryGetJob($"flow-{flowId}-schedule", out var snap));
        Assert.Equal("0 * * * *", snap.EffectiveCron);
    }

    [Fact]
    public void SyncTriggers_DisabledFlow_RemovesJobs()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        var manifest = new FlowManifest
        {
            Triggers =
            {
                ["schedule"] = new TriggerMetadata
                {
                    Type = TriggerType.Cron,
                    Inputs = new Dictionary<string, object?> { ["cronExpression"] = "0 * * * *" }
                }
            }
        };
        flow.Manifest.Returns(manifest);

        var repo = Substitute.For<IFlowRepository>();
        repo.GetAllFlowsAsync()
            .Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new[] { flow }));

        var (dispatcher, _, _) = CreateSut(repo: repo);
        dispatcher.RegisterOrUpdate($"flow-{flowId}-schedule", flowId, "schedule", "0 * * * *");

        // Act
        dispatcher.SyncTriggers(flowId, isEnabled: false);

        // Assert
        Assert.False(dispatcher.TryGetJob($"flow-{flowId}-schedule", out _));
    }

    [Fact]
    public void SyncTriggers_CronOverride_OverridesManifestExpression()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var triggerKey = "schedule";
        var jobId = $"flow-{flowId}-{triggerKey}";

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        var manifest = new FlowManifest
        {
            Triggers =
            {
                [triggerKey] = new TriggerMetadata
                {
                    Type = TriggerType.Cron,
                    Inputs = new Dictionary<string, object?> { ["cronExpression"] = "0 * * * *" }
                }
            }
        };
        flow.Manifest.Returns(manifest);

        var repo = Substitute.For<IFlowRepository>();
        repo.GetAllFlowsAsync()
            .Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new[] { flow }));

        var stateStore = Substitute.For<IFlowScheduleStateStore>();
        stateStore.GetAsync(jobId)
            .Returns(new FlowScheduleState { CronOverride = "*/5 * * * *", IsPaused = false });

        var (dispatcher, _, _) = CreateSut(
            repo: repo,
            scheduleStateStore: stateStore,
            options: new FlowSchedulerOptions { PersistOverrides = true });

        // Act
        dispatcher.SyncTriggers(flowId, isEnabled: true);

        // Assert
        Assert.True(dispatcher.TryGetJob(jobId, out var snap));
        Assert.Equal("*/5 * * * *", snap.EffectiveCron);
    }

    [Fact]
    public void SyncTriggers_PausedOverride_RemovesJob()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var triggerKey = "schedule";
        var jobId = $"flow-{flowId}-{triggerKey}";

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        var manifest = new FlowManifest
        {
            Triggers =
            {
                [triggerKey] = new TriggerMetadata
                {
                    Type = TriggerType.Cron,
                    Inputs = new Dictionary<string, object?> { ["cronExpression"] = "0 * * * *" }
                }
            }
        };
        flow.Manifest.Returns(manifest);

        var repo = Substitute.For<IFlowRepository>();
        repo.GetAllFlowsAsync()
            .Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new[] { flow }));

        var stateStore = Substitute.For<IFlowScheduleStateStore>();
        stateStore.GetAsync(jobId)
            .Returns(new FlowScheduleState { IsPaused = true });

        var (dispatcher, _, _) = CreateSut(
            repo: repo,
            scheduleStateStore: stateStore,
            options: new FlowSchedulerOptions { PersistOverrides = true });

        dispatcher.RegisterOrUpdate(jobId, flowId, triggerKey, "0 * * * *");

        // Act
        dispatcher.SyncTriggers(flowId, isEnabled: true);

        // Assert
        Assert.False(dispatcher.TryGetJob(jobId, out _));
    }

    [Fact]
    public async Task EnqueueTriggerAsync_FiresImmediately_BypassingSchedule()
    {
        // Arrange
        var (dispatcher, orchestrator, _) = CreateSut();
        var flowId = Guid.NewGuid();

        // Act
        await dispatcher.EnqueueTriggerAsync(flowId, "schedule");
        // Allow fire-and-forget task to complete.
        for (var i = 0; i < 50; i++)
        {
            if (orchestrator.ReceivedCalls().Any()) break;
            await Task.Delay(20);
        }

        // Assert
        await orchestrator.Received(1).TriggerByScheduleAsync(
            flowId, "schedule", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartStop_LifecycleIsClean()
    {
        // Arrange
        var (dispatcher, _, sp) = CreateSut();

        // Act + Assert (no exceptions on start/stop)
        await dispatcher.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await dispatcher.StopAsync(CancellationToken.None);
        await dispatcher.DisposeAsync();
    }

    [Fact]
    public void RegisterOrUpdate_ExistingJob_UpdatesFieldsAndClearsPaused()
    {
        // Arrange
        var (dispatcher, _, _) = CreateSut();
        var jobId = "job1";
        var flowId1 = Guid.NewGuid();
        var flowId2 = Guid.NewGuid();
        dispatcher.RegisterOrUpdate(jobId, flowId1, "scheduleA", "0 * * * *");

        // Act — re-register with new flowId/triggerKey/cron
        dispatcher.RegisterOrUpdate(jobId, flowId2, "scheduleB", "*/5 * * * *");

        // Assert — existing entry was updated in-place (no duplicate)
        Assert.True(dispatcher.TryGetJob(jobId, out var snap));
        Assert.Equal(flowId2, snap.FlowId);
        Assert.Equal("scheduleB", snap.TriggerKey);
        Assert.Equal("*/5 * * * *", snap.EffectiveCron);
        Assert.False(snap.Paused);
    }

    [Fact]
    public async Task TriggerOnce_ExistingJob_FiresOrchestrator()
    {
        // Arrange
        var (dispatcher, orchestrator, _) = CreateSut();
        var flowId = Guid.NewGuid();
        dispatcher.RegisterOrUpdate("job1", flowId, "schedule", "0 * * * *");

        // Act
        dispatcher.TriggerOnce("job1");
        // Allow the fire-and-forget Task.Run to complete.
        for (var i = 0; i < 50; i++)
        {
            if (orchestrator.ReceivedCalls().Any()) break;
            await Task.Delay(20);
        }

        // Assert
        await orchestrator.Received(1).TriggerByScheduleAsync(
            flowId, "schedule", "job1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerOnce_UnknownJob_DoesNothing()
    {
        // Arrange
        var (dispatcher, orchestrator, _) = CreateSut();

        // Act
        dispatcher.TriggerOnce("does-not-exist");
        await Task.Delay(50);

        // Assert
        await orchestrator.DidNotReceiveWithAnyArgs()
            .TriggerByScheduleAsync(default, default!, default, default);
    }

    [Fact]
    public async Task EnqueueTriggerAsync_OrchestratorThrows_ExceptionIsSwallowed()
    {
        // Arrange
        var orchestrator = Substitute.For<IFlowOrchestrator>();
        orchestrator
            .TriggerByScheduleAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<object?>>(_ => throw new InvalidOperationException("boom"));

        var services = new ServiceCollection();
        services.AddSingleton(orchestrator);
        using var sp = services.BuildServiceProvider();

        var dispatcher = new PeriodicTimerRecurringTriggerDispatcher(
            sp,
            Substitute.For<IFlowRepository>(),
            Substitute.For<IFlowScheduleStateStore>(),
            new FlowSchedulerOptions(),
            NullLogger<PeriodicTimerRecurringTriggerDispatcher>.Instance);

        // Act
        var ex = await Record.ExceptionAsync(() => dispatcher.EnqueueTriggerAsync(Guid.NewGuid(), "schedule"));
        // Allow fire-and-forget task to complete and swallow.
        for (var i = 0; i < 50; i++)
        {
            if (orchestrator.ReceivedCalls().Any()) break;
            await Task.Delay(20);
        }

        // Assert — caller never sees the exception (logged internally).
        Assert.Null(ex);
        await orchestrator.Received(1).TriggerByScheduleAsync(
            Arg.Any<Guid>(), "schedule", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void SyncTriggers_FlowNotFound_DoesNothing()
    {
        // Arrange
        var repo = Substitute.For<IFlowRepository>();
        repo.GetAllFlowsAsync()
            .Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(Array.Empty<IFlowDefinition>()));

        var (dispatcher, _, _) = CreateSut(repo: repo);

        // Act
        dispatcher.SyncTriggers(Guid.NewGuid(), isEnabled: true);

        // Assert — no jobs registered when flow lookup misses.
        var jobs = dispatcher.GetJobsAsync().GetAwaiter().GetResult();
        Assert.Empty(jobs);
    }

    [Fact]
    public void SyncTriggers_NonCronTrigger_IsIgnored()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        var manifest = new FlowManifest
        {
            Triggers =
            {
                ["webhook"] = new TriggerMetadata { Type = TriggerType.Webhook },
                ["manual"] = new TriggerMetadata { Type = TriggerType.Manual },
            }
        };
        flow.Manifest.Returns(manifest);

        var repo = Substitute.For<IFlowRepository>();
        repo.GetAllFlowsAsync()
            .Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new[] { flow }));

        var (dispatcher, _, _) = CreateSut(repo: repo);

        // Act
        dispatcher.SyncTriggers(flowId, isEnabled: true);

        // Assert — non-Cron triggers are skipped, no jobs registered.
        Assert.False(dispatcher.TryGetJob($"flow-{flowId}-webhook", out _));
        Assert.False(dispatcher.TryGetJob($"flow-{flowId}-manual", out _));
    }

    [Fact]
    public async Task Dispose_Synchronous_StopsLoopWithoutThrowing()
    {
        // Arrange
        var (dispatcher, _, _) = CreateSut();
        await dispatcher.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        // Act
        var ex = Record.Exception(() => dispatcher.Dispose());

        // Assert — synchronous Dispose path stops the loop cleanly.
        Assert.Null(ex);
    }
}
