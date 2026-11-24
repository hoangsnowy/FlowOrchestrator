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
/// Coverage for an unparseable cron expression reaching the in-memory recurring-trigger dispatcher and the
/// runtime-agnostic <see cref="RecurringTriggerSync"/>.
/// </summary>
/// <remarks>
/// <para>
/// The dashboard's <c>PUT /api/schedules/{jobId}/cron</c> endpoint validates only that
/// <c>cronExpression</c> is non-blank: it persists the value as a <see cref="FlowScheduleState.CronOverride"/>
/// and then calls <see cref="IRecurringTriggerDispatcher.RegisterOrUpdate"/> with it.
/// </para>
/// <para>
/// On the in-memory runtime the update path of <c>RegisterOrUpdate</c> assigns the new cron to the live job
/// state BEFORE parsing it. The parse throws, but the job is left carrying the bad expression. The next time
/// the job is due, <c>FireAsync</c> fires the trigger and then re-throws from its <c>finally</c> (the next
/// occurrence cannot be computed). <c>RunLoopAsync</c> awaits <c>FireAsync</c> outside any per-job catch, so
/// the exception ends the shared timer loop: every cron job of every flow stops firing until restart.
/// </para>
/// <para>
/// On restart, <see cref="RecurringTriggerSync"/> reads the persisted override and passes it to
/// <c>RegisterOrUpdate</c>, which throws again; <c>FlowSyncHostedService</c> rethrows, so the host fails to
/// start at all until someone edits the schedule-state table by hand.
/// </para>
/// </remarks>
public sealed class PeriodicTimerCronOverrideValidationTests
{
    private const string ValidCron = "0 0 * * *";
    private const string InvalidCron = "every day at noon";

    private static PeriodicTimerRecurringTriggerDispatcher CreateDispatcher()
    {
        var services = new ServiceCollection()
            .AddSingleton(Substitute.For<IFlowOrchestrator>())
            .BuildServiceProvider();
        return new PeriodicTimerRecurringTriggerDispatcher(
            services, NullLogger<PeriodicTimerRecurringTriggerDispatcher>.Instance);
    }

    private sealed class CronFlow : IFlowDefinition
    {
        public CronFlow(Guid id, string triggerKey, string cron)
        {
            Id = id;
            Manifest = new FlowManifest();
            Manifest.Triggers[triggerKey] = new TriggerMetadata
            {
                Type = TriggerType.Cron,
                Inputs = new Dictionary<string, object?> { ["cronExpression"] = cron },
            };
        }

        public Guid Id { get; }
        public string Version => "1.0";
        public FlowManifest Manifest { get; set; }
    }

    [Fact]
    public async Task RegisterOrUpdate_RejectedCronOnExistingJob_LeavesTheJobFireableOnItsPreviousSchedule()
    {
        // Arrange — a healthy job, then the dashboard pushes an unparseable override for it.
        var flowId = Guid.NewGuid();
        var jobId = RecurringTriggerJobId.Create(flowId, "nightly");
        var sut = CreateDispatcher();
        sut.RegisterOrUpdate(jobId, flowId, "nightly", ValidCron);
        _ = Record.Exception(() => sut.RegisterOrUpdate(jobId, flowId, "nightly", InvalidCron));

        // Act — the timer loop fires the job when it is next due (FireAsync is what RunLoopAsync awaits).
        var fireFailure = await Record.ExceptionAsync(() => sut.FireForTestAsync(jobId));

        // Assert — the rejected update must not have poisoned the job, and firing must not throw
        // (a throw here escapes RunLoopAsync and stops every cron job in the process).
        Assert.Null(fireFailure);
        Assert.True(sut.TryGetJob(jobId, out var job));
        Assert.Equal(ValidCron, job.EffectiveCron);
    }

    [Fact]
    public async Task SyncTriggersAsync_PersistedOverrideIsUnparseable_DoesNotAbortStartupAndKeepsAJobScheduled()
    {
        // Arrange — the override the dashboard accepted and persisted before the restart.
        var flowId = Guid.NewGuid();
        var jobId = RecurringTriggerJobId.Create(flowId, "nightly");
        var stateStore = new InMemoryFlowScheduleStateStore();
        await stateStore.SaveAsync(new FlowScheduleState
        {
            JobId = jobId,
            FlowId = flowId,
            FlowName = "CronFlow",
            TriggerKey = "nightly",
            CronOverride = InvalidCron,
        });
        var dispatcher = CreateDispatcher();
        var sync = new RecurringTriggerSync(
            new FlowRegistry([new CronFlow(flowId, "nightly", ValidCron)]),
            dispatcher,
            stateStore,
            new FlowSchedulerOptions { PersistOverrides = true });

        // Act — FlowSyncHostedService.StartAsync runs exactly this and rethrows any failure.
        var failure = await Record.ExceptionAsync(() => sync.SyncTriggersAsync(flowId, isEnabled: true));

        // Assert — a bad persisted override must not stop the host from starting.
        Assert.Null(failure);
        Assert.True(dispatcher.TryGetJob(jobId, out _));
    }

    [Fact]
    public void RegisterOrUpdate_UnparseableCronOnExistingJob_ThrowsArgumentExceptionAndKeepsThePreviousCron()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var jobId = RecurringTriggerJobId.Create(flowId, "nightly");
        var sut = CreateDispatcher();
        sut.RegisterOrUpdate(jobId, flowId, "nightly", ValidCron);
        Assert.True(sut.TryGetJob(jobId, out var before));

        // Act
        var failure = Record.Exception(() => sut.RegisterOrUpdate(jobId, flowId, "nightly", InvalidCron));

        // Assert — a clear ArgumentException for the caller, and the live job is untouched.
        Assert.IsType<ArgumentException>(failure);
        Assert.True(sut.TryGetJob(jobId, out var after));
        Assert.Equal(ValidCron, after.EffectiveCron);
        Assert.Equal(before.NextExecution, after.NextExecution);
    }

    [Fact]
    public void RegisterOrUpdate_UnparseableCronOnNewJob_ThrowsArgumentExceptionAndRegistersNothing()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var jobId = RecurringTriggerJobId.Create(flowId, "nightly");
        var sut = CreateDispatcher();

        // Act
        var failure = Record.Exception(() => sut.RegisterOrUpdate(jobId, flowId, "nightly", InvalidCron));

        // Assert
        Assert.IsType<ArgumentException>(failure);
        Assert.False(sut.TryGetJob(jobId, out _));
    }


    [Fact]
    public async Task SyncTriggersAsync_PersistedOverrideIsUnparseable_FallsBackToTheManifestCron()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var jobId = RecurringTriggerJobId.Create(flowId, "nightly");
        var stateStore = new InMemoryFlowScheduleStateStore();
        await stateStore.SaveAsync(new FlowScheduleState
        {
            JobId = jobId,
            FlowId = flowId,
            FlowName = "CronFlow",
            TriggerKey = "nightly",
            CronOverride = InvalidCron,
        });
        var dispatcher = CreateDispatcher();
        var sync = new RecurringTriggerSync(
            new FlowRegistry([new CronFlow(flowId, "nightly", ValidCron)]),
            dispatcher,
            stateStore,
            new FlowSchedulerOptions { PersistOverrides = true });

        // Act
        await sync.SyncTriggersAsync(flowId, isEnabled: true);

        // Assert
        Assert.True(dispatcher.TryGetJob(jobId, out var job));
        Assert.Equal(ValidCron, job.EffectiveCron);
    }
}
