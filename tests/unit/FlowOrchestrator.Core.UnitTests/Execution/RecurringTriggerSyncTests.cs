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
}
