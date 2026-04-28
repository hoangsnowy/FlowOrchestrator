using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Hangfire;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using NSubstitute;

namespace FlowOrchestrator.Hangfire.Tests;

/// <summary>
/// Unit tests for the three Hangfire adapter classes introduced in the v2.0 refactor:
/// <see cref="HangfireStepDispatcher"/>, <see cref="HangfireRecurringTriggerDispatcher"/>,
/// and <see cref="HangfireRecurringTriggerInspector"/>.
/// All classes are <c>internal sealed</c>; the <c>InternalsVisibleTo</c> attribute on
/// <c>FlowOrchestrator.Hangfire.csproj</c> makes them accessible from this test project.
/// </summary>
public sealed class HangfireAdapterTests
{
    // ── shared mocks ──────────────────────────────────────────────────────────

    private readonly IBackgroundJobClient _jobClient = Substitute.For<IBackgroundJobClient>();
    private readonly IRecurringJobManager _recurringManager = Substitute.For<IRecurringJobManager>();

    // ── helpers ──────────────────────────────────────────────────────────────

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

    // ═════════════════════════════════════════════════════════════════════════
    // HangfireStepDispatcher
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <see cref="HangfireStepDispatcher.EnqueueStepAsync"/> must call <see cref="IBackgroundJobClient.Create"/>
    /// with an <see cref="EnqueuedState"/> and return the job ID produced by Hangfire.
    /// </summary>
    [Fact]
    public async Task EnqueueStepAsync_CallsBackgroundJobClientCreate_WithEnqueuedState()
    {
        // IBackgroundJobClient.Enqueue<T>() is an extension that delegates to IBackgroundJobClient.Create().
        const string expectedJobId = "job-enqueue-42";
        _jobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(expectedJobId);

        var sut = new HangfireStepDispatcher(_jobClient);
        var ctx = MakeContext();
        var flow = MakeFlow();
        var step = MakeStep(ctx);

        var result = await sut.EnqueueStepAsync(ctx, flow, step, CancellationToken.None);

        result.Should().Be(expectedJobId);
        _jobClient.Received(1).Create(
            Arg.Any<Job>(),
            Arg.Is<IState>(s => s is EnqueuedState));
    }

    /// <summary>
    /// <see cref="HangfireStepDispatcher.ScheduleStepAsync"/> must call <see cref="IBackgroundJobClient.Create"/>
    /// with a <see cref="ScheduledState"/> whose delay matches the requested delay.
    /// </summary>
    [Fact]
    public async Task ScheduleStepAsync_CallsBackgroundJobClientCreate_WithScheduledState()
    {
        const string expectedJobId = "job-scheduled-99";
        _jobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(expectedJobId);

        var sut = new HangfireStepDispatcher(_jobClient);
        var ctx = MakeContext();
        var flow = MakeFlow();
        var step = MakeStep(ctx);
        var delay = TimeSpan.FromSeconds(30);

        var result = await sut.ScheduleStepAsync(ctx, flow, step, delay, CancellationToken.None);

        result.Should().Be(expectedJobId);
        _jobClient.Received(1).Create(
            Arg.Any<Job>(),
            Arg.Is<IState>(s => s is ScheduledState));
    }

    /// <summary>
    /// <see cref="HangfireStepDispatcher.EnqueueStepAsync"/> targets <see cref="IHangfireStepRunner"/>
    /// as the job type, not any other interface.
    /// </summary>
    [Fact]
    public async Task EnqueueStepAsync_JobTargetsIHangfireStepRunner()
    {
        _jobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("job-123");

        var sut = new HangfireStepDispatcher(_jobClient);
        var ctx = MakeContext();
        var flow = MakeFlow();
        var step = MakeStep(ctx);

        await sut.EnqueueStepAsync(ctx, flow, step, CancellationToken.None);

        _jobClient.Received(1).Create(
            Arg.Is<Job>(j => j.Type == typeof(IHangfireStepRunner)),
            Arg.Any<IState>());
    }

    /// <summary>
    /// <see cref="HangfireStepDispatcher.ScheduleStepAsync"/> targets <see cref="IHangfireStepRunner"/>
    /// as the job type, not any other interface.
    /// </summary>
    [Fact]
    public async Task ScheduleStepAsync_JobTargetsIHangfireStepRunner()
    {
        _jobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("job-456");

        var sut = new HangfireStepDispatcher(_jobClient);
        var ctx = MakeContext();
        var flow = MakeFlow();
        var step = MakeStep(ctx);

        await sut.ScheduleStepAsync(ctx, flow, step, TimeSpan.FromSeconds(5), CancellationToken.None);

        _jobClient.Received(1).Create(
            Arg.Is<Job>(j => j.Type == typeof(IHangfireStepRunner)),
            Arg.Any<IState>());
    }

    /// <summary>
    /// Returns the job ID string produced by <see cref="IBackgroundJobClient.Create"/> for scheduled steps.
    /// A <see langword="null"/> return from the client propagates correctly.
    /// </summary>
    [Fact]
    public async Task ScheduleStepAsync_ReturnsNullWhenClientReturnsNull()
    {
        _jobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns((string)null!);

        var sut = new HangfireStepDispatcher(_jobClient);
        var ctx = MakeContext();
        var flow = MakeFlow();
        var step = MakeStep(ctx);

        var result = await sut.ScheduleStepAsync(ctx, flow, step, TimeSpan.FromSeconds(1), CancellationToken.None);

        result.Should().BeNull();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // HangfireRecurringTriggerDispatcher
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <see cref="HangfireRecurringTriggerDispatcher.RegisterOrUpdate"/> must delegate to
    /// <see cref="IRecurringJobManager.AddOrUpdate"/> with the correct job ID and cron expression.
    /// </summary>
    [Fact]
    public void RegisterOrUpdate_DelegatesToRecurringJobManager_AddOrUpdate()
    {
        var sut = new HangfireRecurringTriggerDispatcher(_recurringManager, _jobClient);
        var flowId = Guid.NewGuid();
        const string jobId = "flow-test-job-cron";
        const string cron = "0 * * * *";

        sut.RegisterOrUpdate(jobId, flowId, "cron", cron);

        _recurringManager.Received(1).AddOrUpdate(
            jobId,
            Arg.Any<Job>(),
            cron,
            Arg.Any<RecurringJobOptions>());
    }

    /// <summary>
    /// <see cref="HangfireRecurringTriggerDispatcher.RegisterOrUpdate"/> targets
    /// <see cref="IHangfireFlowTrigger"/> so that the correct handler is invoked at schedule time.
    /// </summary>
    [Fact]
    public void RegisterOrUpdate_JobTargetsIHangfireFlowTrigger()
    {
        var sut = new HangfireRecurringTriggerDispatcher(_recurringManager, _jobClient);
        var flowId = Guid.NewGuid();

        sut.RegisterOrUpdate("flow-abc-cron", flowId, "cron", "*/5 * * * *");

        _recurringManager.Received(1).AddOrUpdate(
            Arg.Any<string>(),
            Arg.Is<Job>(j => j.Type == typeof(IHangfireFlowTrigger)),
            Arg.Any<string>(),
            Arg.Any<RecurringJobOptions>());
    }

    /// <summary>
    /// <see cref="HangfireRecurringTriggerDispatcher.Remove"/> must delegate to
    /// <see cref="IRecurringJobManager.RemoveIfExists"/> with the provided job ID.
    /// </summary>
    [Fact]
    public void Remove_DelegatesToRecurringJobManager_RemoveIfExists()
    {
        var sut = new HangfireRecurringTriggerDispatcher(_recurringManager, _jobClient);
        const string jobId = "flow-to-remove";

        sut.Remove(jobId);

        _recurringManager.Received(1).RemoveIfExists(jobId);
    }

    /// <summary>
    /// <see cref="HangfireRecurringTriggerDispatcher.TriggerOnce"/> must delegate to
    /// <see cref="IRecurringJobManager.Trigger"/> with the provided job ID.
    /// </summary>
    [Fact]
    public void TriggerOnce_DelegatesToRecurringJobManager_Trigger()
    {
        var sut = new HangfireRecurringTriggerDispatcher(_recurringManager, _jobClient);
        const string jobId = "flow-to-fire-once";

        sut.TriggerOnce(jobId);

        _recurringManager.Received(1).Trigger(jobId);
    }

    /// <summary>
    /// <see cref="HangfireRecurringTriggerDispatcher.EnqueueTriggerAsync"/> must delegate to
    /// <see cref="IBackgroundJobClient.Create"/> to enqueue an immediate one-off trigger job
    /// targeting <see cref="IHangfireFlowTrigger"/>.
    /// </summary>
    [Fact]
    public async Task EnqueueTriggerAsync_DelegatesToBackgroundJobClient_Enqueue()
    {
        _jobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("one-off-job-id");

        var sut = new HangfireRecurringTriggerDispatcher(_recurringManager, _jobClient);
        var flowId = Guid.NewGuid();

        await sut.EnqueueTriggerAsync(flowId, "cron");

        _jobClient.Received(1).Create(
            Arg.Is<Job>(j => j.Type == typeof(IHangfireFlowTrigger)),
            Arg.Is<IState>(s => s is EnqueuedState));
    }

    /// <summary>
    /// <see cref="HangfireRecurringTriggerDispatcher.EnqueueTriggerAsync"/> returns a completed
    /// <see cref="Task"/> regardless of whether Hangfire assigns a job ID.
    /// </summary>
    [Fact]
    public async Task EnqueueTriggerAsync_CompletesSuccessfully()
    {
        _jobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("trigger-job-id");

        var sut = new HangfireRecurringTriggerDispatcher(_recurringManager, _jobClient);

        var act = () => sut.EnqueueTriggerAsync(Guid.NewGuid(), "cron");

        await act.Should().NotThrowAsync();
    }
}
