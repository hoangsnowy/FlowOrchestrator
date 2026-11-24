using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Decision table of <see cref="RecurringTriggerSync"/> — the single effective-cron resolution that
/// replaced the per-runtime copies in Hangfire, the in-memory runtime and Service Bus.
/// </summary>
public sealed class RecurringTriggerSyncTests
{
    private const string ManifestCron = "0 * * * *";

    private readonly Guid _flowId = Guid.NewGuid();
    private readonly IRecurringTriggerDispatcher _dispatcher = Substitute.For<IRecurringTriggerDispatcher>();
    private readonly InMemoryFlowScheduleStateStore _stateStore = new();

    private string JobId(string triggerKey = "schedule") => RecurringTriggerJobId.Create(_flowId, triggerKey);

    private RecurringTriggerSync CreateSut(FlowManifest manifest, bool persistOverrides = true)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(_flowId);
        flow.Manifest.Returns(manifest);
        return new RecurringTriggerSync(
            new FlowRegistry([flow]),
            _dispatcher,
            _stateStore,
            new FlowSchedulerOptions { PersistOverrides = persistOverrides });
    }

    private static FlowManifest CronManifest(string? cron = ManifestCron)
    {
        var inputs = new Dictionary<string, object?>();
        if (cron is not null)
        {
            inputs["cronExpression"] = cron;
        }

        return new FlowManifest
        {
            Triggers =
            {
                ["schedule"] = new TriggerMetadata { Type = TriggerType.Cron, Inputs = inputs },
                ["hook"] = new TriggerMetadata { Type = TriggerType.Webhook },
            }
        };
    }

    [Fact]
    public async Task EnabledFlow_NoPersistedState_RegistersManifestCron()
    {
        // Arrange
        var sut = CreateSut(CronManifest());

        // Act
        await sut.SyncTriggersAsync(_flowId, isEnabled: true);

        // Assert — only the cron trigger is touched; the webhook trigger is ignored.
        _dispatcher.Received(1).RegisterOrUpdate(JobId(), _flowId, "schedule", ManifestCron);
        _dispatcher.DidNotReceive().Remove(Arg.Any<string>());
        _dispatcher.DidNotReceive().RegisterOrUpdate(JobId("hook"), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task EnabledFlow_PersistedOverride_RegistersOverrideCron()
    {
        // Arrange
        var sut = CreateSut(CronManifest());
        await _stateStore.SaveAsync(new FlowScheduleState { JobId = JobId(), CronOverride = "*/5 * * * *" });

        // Act
        await sut.SyncTriggersAsync(_flowId, isEnabled: true);

        // Assert
        _dispatcher.Received(1).RegisterOrUpdate(JobId(), _flowId, "schedule", "*/5 * * * *");
    }

    [Fact]
    public async Task EnabledFlow_PersistedPause_RemovesJob()
    {
        // Arrange
        var sut = CreateSut(CronManifest());
        await _stateStore.SaveAsync(new FlowScheduleState { JobId = JobId(), IsPaused = true, CronOverride = "*/5 * * * *" });

        // Act
        await sut.SyncTriggersAsync(_flowId, isEnabled: true);

        // Assert
        _dispatcher.Received(1).Remove(JobId());
        _dispatcher.DidNotReceiveWithAnyArgs().RegisterOrUpdate(default!, default, default!, default!);
    }

    [Fact]
    public async Task PersistOverridesDisabled_IgnoresStoredPauseAndOverride()
    {
        // Arrange
        var sut = CreateSut(CronManifest(), persistOverrides: false);
        await _stateStore.SaveAsync(new FlowScheduleState { JobId = JobId(), IsPaused = true, CronOverride = "*/5 * * * *" });

        // Act
        await sut.SyncTriggersAsync(_flowId, isEnabled: true);

        // Assert
        _dispatcher.Received(1).RegisterOrUpdate(JobId(), _flowId, "schedule", ManifestCron);
        _dispatcher.DidNotReceive().Remove(Arg.Any<string>());
    }

    [Fact]
    public async Task DisabledFlow_RemovesJob_EvenWithAnOverride()
    {
        // Arrange
        var sut = CreateSut(CronManifest());
        await _stateStore.SaveAsync(new FlowScheduleState { JobId = JobId(), CronOverride = "*/5 * * * *" });

        // Act
        await sut.SyncTriggersAsync(_flowId, isEnabled: false);

        // Assert
        _dispatcher.Received(1).Remove(JobId());
        _dispatcher.DidNotReceiveWithAnyArgs().RegisterOrUpdate(default!, default, default!, default!);
    }

    [Fact]
    public async Task CronTriggerWithoutExpression_RemovesJob()
    {
        // Arrange
        var sut = CreateSut(CronManifest(cron: null));

        // Act
        await sut.SyncTriggersAsync(_flowId, isEnabled: true);

        // Assert
        _dispatcher.Received(1).Remove(JobId());
        _dispatcher.DidNotReceiveWithAnyArgs().RegisterOrUpdate(default!, default, default!, default!);
    }

    [Fact]
    public async Task UnregisteredFlow_IsANoOp()
    {
        // Arrange
        var sut = CreateSut(CronManifest());

        // Act
        await sut.SyncTriggersAsync(Guid.NewGuid(), isEnabled: true);

        // Assert
        Assert.Empty(_dispatcher.ReceivedCalls());
    }

    [Fact]
    public async Task EnabledFlow_RuntimeRejectsPersistedOverride_FallsBackToManifestCron()
    {
        // Arrange — the dispatcher rejects the override the way Hangfire's AddOrUpdate does.
        const string badOverride = "every day at noon";
        var sut = CreateSut(CronManifest());
        await _stateStore.SaveAsync(new FlowScheduleState { JobId = JobId(), CronOverride = badOverride });
        _dispatcher
            .When(d => d.RegisterOrUpdate(JobId(), _flowId, "schedule", badOverride))
            .Do(_ => throw new ArgumentException("CRON expression is invalid.", "cronExpression"));

        // Act
        var failure = await Record.ExceptionAsync(() => sut.SyncTriggersAsync(_flowId, isEnabled: true));

        // Assert — startup is not aborted, and the job still runs on the manifest schedule.
        Assert.Null(failure);
        _dispatcher.Received(1).RegisterOrUpdate(JobId(), _flowId, "schedule", ManifestCron);
    }

    [Fact]
    public async Task EnabledFlow_RuntimeRejectsManifestCron_StillPropagates()
    {
        // Arrange — an unparseable cron in code is an authoring error, not a stale persisted value.
        var sut = CreateSut(CronManifest("not a cron"));
        _dispatcher
            .When(d => d.RegisterOrUpdate(JobId(), _flowId, "schedule", "not a cron"))
            .Do(_ => throw new ArgumentException("CRON expression is invalid.", "cronExpression"));

        // Act
        var failure = await Record.ExceptionAsync(() => sut.SyncTriggersAsync(_flowId, isEnabled: true));

        // Assert
        Assert.IsType<ArgumentException>(failure);
    }

    [Fact]
    public async Task EnabledFlow_RuntimeFailsForAnotherReason_DoesNotMaskTheFailure()
    {
        // Arrange — only a cron rejection is absorbed; a storage outage in the runtime is not.
        var sut = CreateSut(CronManifest());
        await _stateStore.SaveAsync(new FlowScheduleState { JobId = JobId(), CronOverride = "*/5 * * * *" });
        _dispatcher
            .When(d => d.RegisterOrUpdate(JobId(), _flowId, "schedule", "*/5 * * * *"))
            .Do(_ => throw new InvalidOperationException("job storage unavailable"));

        // Act
        var failure = await Record.ExceptionAsync(() => sut.SyncTriggersAsync(_flowId, isEnabled: true));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
        _dispatcher.DidNotReceive().RegisterOrUpdate(JobId(), _flowId, "schedule", ManifestCron);
    }
}
