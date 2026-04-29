using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Hangfire;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using NSubstitute;

namespace FlowOrchestrator.Hangfire.Tests;

/// <summary>
/// Unit tests for the three Hangfire adapter classes introduced in the v2.0 refactor:
/// <see cref="HangfireStepDispatcher"/>, <see cref="HangfireRecurringTriggerDispatcher"/>,
/// and <see cref="HangfireRecurringTriggerInspector"/>.
/// </summary>
public sealed class HangfireAdapterTests
{
    private readonly IBackgroundJobClient _jobClient = Substitute.For<IBackgroundJobClient>();
    private readonly IRecurringJobManager _recurringManager = Substitute.For<IRecurringJobManager>();

    private static Core.Execution.ExecutionContext MakeContext() =>
        new() { RunId = Guid.NewGuid() };

    private static IFlowDefinition MakeFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection { ["step1"] = new StepMetadata { Type = "DoWork" } }
        });
        return flow;
    }

    private static StepInstance MakeStep(Core.Execution.ExecutionContext ctx) =>
        new("step1", "DoWork") { RunId = ctx.RunId };

    [Fact]
    public async Task EnqueueStepAsync_CallsBackgroundJobClientCreate_WithEnqueuedState()
    {
        // Arrange
        const string expectedJobId = "job-enqueue-42";
        _jobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(expectedJobId);
        var sut = new HangfireStepDispatcher(_jobClient);
        var ctx = MakeContext();
        var flow = MakeFlow();
        var step = MakeStep(ctx);

        // Act
        var result = await sut.EnqueueStepAsync(ctx, flow, step, CancellationToken.None);

        // Assert
        Assert.Equal(expectedJobId, result);
        _jobClient.Received(1).Create(
            Arg.Any<Job>(),
            Arg.Is<IState>(s => s is EnqueuedState));
    }

    [Fact]
    public async Task ScheduleStepAsync_CallsBackgroundJobClientCreate_WithScheduledState()
    {
        // Arrange
        const string expectedJobId = "job-scheduled-99";
        _jobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(expectedJobId);
        var sut = new HangfireStepDispatcher(_jobClient);
        var ctx = MakeContext();
        var flow = MakeFlow();
        var step = MakeStep(ctx);
        var delay = TimeSpan.FromSeconds(30);

        // Act
        var result = await sut.ScheduleStepAsync(ctx, flow, step, delay, CancellationToken.None);

        // Assert
        Assert.Equal(expectedJobId, result);
        _jobClient.Received(1).Create(
            Arg.Any<Job>(),
            Arg.Is<IState>(s => s is ScheduledState));
    }

    [Fact]
    public async Task EnqueueStepAsync_JobTargetsIHangfireStepRunner()
    {
        // Arrange
        _jobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("job-123");
        var sut = new HangfireStepDispatcher(_jobClient);
        var ctx = MakeContext();
        var flow = MakeFlow();
        var step = MakeStep(ctx);

        // Act
        await sut.EnqueueStepAsync(ctx, flow, step, CancellationToken.None);

        // Assert
        _jobClient.Received(1).Create(
            Arg.Is<Job>(j => j.Type == typeof(IHangfireStepRunner)),
            Arg.Any<IState>());
    }

    [Fact]
    public async Task ScheduleStepAsync_JobTargetsIHangfireStepRunner()
    {
        // Arrange
        _jobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("job-456");
        var sut = new HangfireStepDispatcher(_jobClient);
        var ctx = MakeContext();
        var flow = MakeFlow();
        var step = MakeStep(ctx);

        // Act
        await sut.ScheduleStepAsync(ctx, flow, step, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Assert
        _jobClient.Received(1).Create(
            Arg.Is<Job>(j => j.Type == typeof(IHangfireStepRunner)),
            Arg.Any<IState>());
    }

    [Fact]
    public async Task ScheduleStepAsync_ReturnsNullWhenClientReturnsNull()
    {
        // Arrange
        _jobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns((string)null!);
        var sut = new HangfireStepDispatcher(_jobClient);
        var ctx = MakeContext();
        var flow = MakeFlow();
        var step = MakeStep(ctx);

        // Act
        var result = await sut.ScheduleStepAsync(ctx, flow, step, TimeSpan.FromSeconds(1), CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void RegisterOrUpdate_DelegatesToRecurringJobManager_AddOrUpdate()
    {
        // Arrange
        var sut = new HangfireRecurringTriggerDispatcher(_recurringManager, _jobClient);
        var flowId = Guid.NewGuid();
        const string jobId = "flow-test-job-cron";
        const string cron = "0 * * * *";

        // Act
        sut.RegisterOrUpdate(jobId, flowId, "cron", cron);

        // Assert
        _recurringManager.Received(1).AddOrUpdate(
            jobId,
            Arg.Any<Job>(),
            cron,
            Arg.Any<RecurringJobOptions>());
    }

    [Fact]
    public void RegisterOrUpdate_JobTargetsIHangfireFlowTrigger()
    {
        // Arrange
        var sut = new HangfireRecurringTriggerDispatcher(_recurringManager, _jobClient);
        var flowId = Guid.NewGuid();

        // Act
        sut.RegisterOrUpdate("flow-abc-cron", flowId, "cron", "*/5 * * * *");

        // Assert
        _recurringManager.Received(1).AddOrUpdate(
            Arg.Any<string>(),
            Arg.Is<Job>(j => j.Type == typeof(IHangfireFlowTrigger)),
            Arg.Any<string>(),
            Arg.Any<RecurringJobOptions>());
    }

    [Fact]
    public void Remove_DelegatesToRecurringJobManager_RemoveIfExists()
    {
        // Arrange
        var sut = new HangfireRecurringTriggerDispatcher(_recurringManager, _jobClient);
        const string jobId = "flow-to-remove";

        // Act
        sut.Remove(jobId);

        // Assert
        _recurringManager.Received(1).RemoveIfExists(jobId);
    }

    [Fact]
    public void TriggerOnce_DelegatesToRecurringJobManager_Trigger()
    {
        // Arrange
        var sut = new HangfireRecurringTriggerDispatcher(_recurringManager, _jobClient);
        const string jobId = "flow-to-fire-once";

        // Act
        sut.TriggerOnce(jobId);

        // Assert
        _recurringManager.Received(1).Trigger(jobId);
    }

    [Fact]
    public async Task EnqueueTriggerAsync_DelegatesToBackgroundJobClient_Enqueue()
    {
        // Arrange
        _jobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("one-off-job-id");
        var sut = new HangfireRecurringTriggerDispatcher(_recurringManager, _jobClient);
        var flowId = Guid.NewGuid();

        // Act
        await sut.EnqueueTriggerAsync(flowId, "cron");

        // Assert
        _jobClient.Received(1).Create(
            Arg.Is<Job>(j => j.Type == typeof(IHangfireFlowTrigger)),
            Arg.Is<IState>(s => s is EnqueuedState));
    }

    [Fact]
    public async Task EnqueueTriggerAsync_CompletesSuccessfully()
    {
        // Arrange
        _jobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("trigger-job-id");
        var sut = new HangfireRecurringTriggerDispatcher(_recurringManager, _jobClient);
        var act = () => sut.EnqueueTriggerAsync(Guid.NewGuid(), "cron");

        // Act
        var ex = await Record.ExceptionAsync(act);

        // Assert
        Assert.Null(ex);
    }
}
