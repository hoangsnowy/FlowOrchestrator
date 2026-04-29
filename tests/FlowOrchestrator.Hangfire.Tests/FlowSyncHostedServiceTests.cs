using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlowOrchestrator.Hangfire.Tests;

public class FlowSyncHostedServiceTests
{
    private static readonly ILogger<FlowSyncHostedService> Logger =
        NullLoggerFactory.Instance.CreateLogger<FlowSyncHostedService>();

    private readonly IRecurringJobManager _recurringJobManager = Substitute.For<IRecurringJobManager>();
    private readonly IFlowScheduleStateStore _scheduleStateStore = Substitute.For<IFlowScheduleStateStore>();
    private readonly IFlowGraphPlanner _graphPlanner = new FlowGraphPlanner();

    private FlowSyncHostedService CreateSut(IFlowRepository repository, IFlowStore store, FlowSchedulerOptions? options = null)
        => new(repository, store, _recurringJobManager, _scheduleStateStore, _graphPlanner, options ?? new FlowSchedulerOptions(), Logger);

    [Fact]
    public async Task StartAsync_SyncsFlowsToStore()
    {
        // Arrange
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Version.Returns("1.0");
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["step1"] = new StepMetadata { Type = "LogMessage" }
            }
        });

        var repository = Substitute.For<IFlowRepository>();
        repository.GetAllFlowsAsync().Returns(new List<IFlowDefinition> { flow });

        var store = Substitute.For<IFlowStore>();
        store.GetByIdAsync(flow.Id).Returns((FlowDefinitionRecord?)null);
        store.SaveAsync(Arg.Any<FlowDefinitionRecord>()).Returns(ci => ci.Arg<FlowDefinitionRecord>());

        var sut = CreateSut(repository, store);

        // Act
        await sut.StartAsync(CancellationToken.None);

        // Assert
        await store.Received(1).SaveAsync(Arg.Is<FlowDefinitionRecord>(r =>
            r.Id == flow.Id && r.Version == "1.0" && r.ManifestJson != null));
    }

    [Fact]
    public async Task StartAsync_UpdatesExistingFlow()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Version.Returns("2.0");
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["step1"] = new StepMetadata { Type = "LogMessage" }
            }
        });

        var existing = new FlowDefinitionRecord
        {
            Id = flowId,
            Name = "OldName",
            Version = "1.0"
        };

        var repository = Substitute.For<IFlowRepository>();
        repository.GetAllFlowsAsync().Returns(new List<IFlowDefinition> { flow });

        var store = Substitute.For<IFlowStore>();
        store.GetByIdAsync(flowId).Returns(existing);
        store.SaveAsync(Arg.Any<FlowDefinitionRecord>()).Returns(ci => ci.Arg<FlowDefinitionRecord>());

        var sut = CreateSut(repository, store);

        // Act
        await sut.StartAsync(CancellationToken.None);

        // Assert
        await store.Received(1).SaveAsync(Arg.Is<FlowDefinitionRecord>(r =>
            r.Id == flowId && r.Version == "2.0"));
    }

    [Fact]
    public async Task StartAsync_FlowSyncFailure_Throws()
    {
        // Arrange
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest());

        var repository = Substitute.For<IFlowRepository>();
        repository.GetAllFlowsAsync().Returns(new List<IFlowDefinition> { flow });

        var store = Substitute.For<IFlowStore>();
        store.GetByIdAsync(Arg.Any<Guid>()).Returns(Task.FromException<FlowDefinitionRecord?>(new Exception("DB error")));

        var sut = CreateSut(repository, store);

        // Act
        var act = () => sut.StartAsync(CancellationToken.None);

        // Assert
        var ex = await Record.ExceptionAsync(act);
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task StopAsync_CompletesImmediately()
    {
        // Arrange
        var repository = Substitute.For<IFlowRepository>();
        var store = Substitute.For<IFlowStore>();
        var sut = CreateSut(repository, store);

        // Act
        var ex = await Record.ExceptionAsync(() => sut.StopAsync(CancellationToken.None));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public async Task StartAsync_RegistersRecurringJobForCronTrigger()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Version.Returns("1.0");
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["scheduled"] = new TriggerMetadata
                {
                    Type = TriggerType.Cron,
                    Inputs = new Dictionary<string, object?> { ["cronExpression"] = "*/10 * * * *" }
                }
            },
            Steps = new StepCollection
            {
                ["step1"] = new StepMetadata { Type = "LogMessage" }
            }
        });

        var repository = Substitute.For<IFlowRepository>();
        repository.GetAllFlowsAsync().Returns(new List<IFlowDefinition> { flow });

        var record = new FlowDefinitionRecord { Id = flowId, IsEnabled = true };
        var store = Substitute.For<IFlowStore>();
        store.GetByIdAsync(flowId).Returns(record);
        store.SaveAsync(Arg.Any<FlowDefinitionRecord>()).Returns(ci => ci.Arg<FlowDefinitionRecord>());

        var sut = CreateSut(repository, store);

        // Act
        await sut.StartAsync(CancellationToken.None);

        // Assert
        _recurringJobManager.Received(1).AddOrUpdate(
            $"flow-{flowId}-scheduled",
            Arg.Any<global::Hangfire.Common.Job>(),
            "*/10 * * * *",
            Arg.Any<global::Hangfire.RecurringJobOptions>());
    }

    [Fact]
    public async Task StartAsync_RemovesRecurringJobForDisabledFlow()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Version.Returns("1.0");
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["scheduled"] = new TriggerMetadata
                {
                    Type = TriggerType.Cron,
                    Inputs = new Dictionary<string, object?> { ["cronExpression"] = "*/10 * * * *" }
                }
            },
            Steps = new StepCollection
            {
                ["step1"] = new StepMetadata { Type = "LogMessage" }
            }
        });

        var repository = Substitute.For<IFlowRepository>();
        repository.GetAllFlowsAsync().Returns(new List<IFlowDefinition> { flow });

        var record = new FlowDefinitionRecord { Id = flowId, IsEnabled = false };
        var store = Substitute.For<IFlowStore>();
        store.GetByIdAsync(flowId).Returns(record);
        store.SaveAsync(Arg.Any<FlowDefinitionRecord>()).Returns(ci => ci.Arg<FlowDefinitionRecord>());

        var sut = CreateSut(repository, store);

        // Act
        await sut.StartAsync(CancellationToken.None);

        // Assert
        _recurringJobManager.Received(1).RemoveIfExists($"flow-{flowId}-scheduled");
    }

    [Fact]
    public async Task StartAsync_RespectsPausedScheduleState()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Version.Returns("1.0");
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["scheduled"] = new TriggerMetadata
                {
                    Type = TriggerType.Cron,
                    Inputs = new Dictionary<string, object?> { ["cronExpression"] = "*/10 * * * *" }
                }
            },
            Steps = new StepCollection { ["step1"] = new StepMetadata { Type = "LogMessage" } }
        });

        var repository = Substitute.For<IFlowRepository>();
        repository.GetAllFlowsAsync().Returns(new List<IFlowDefinition> { flow });

        var store = Substitute.For<IFlowStore>();
        store.GetByIdAsync(flowId).Returns(new FlowDefinitionRecord { Id = flowId, IsEnabled = true });
        store.SaveAsync(Arg.Any<FlowDefinitionRecord>()).Returns(ci => ci.Arg<FlowDefinitionRecord>());

        _scheduleStateStore.GetAsync($"flow-{flowId}-scheduled")
            .Returns(new FlowScheduleState { JobId = $"flow-{flowId}-scheduled", IsPaused = true });

        var sut = CreateSut(repository, store);

        // Act
        await sut.StartAsync(CancellationToken.None);

        // Assert
        _recurringJobManager.Received(1).RemoveIfExists($"flow-{flowId}-scheduled");
        _recurringJobManager.DidNotReceive().AddOrUpdate(
            Arg.Any<string>(),
            Arg.Any<global::Hangfire.Common.Job>(),
            Arg.Any<string>(),
            Arg.Any<global::Hangfire.RecurringJobOptions>());
    }

    [Fact]
    public async Task StartAsync_UsesCronOverride_WhenPresent()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Version.Returns("1.0");
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["scheduled"] = new TriggerMetadata
                {
                    Type = TriggerType.Cron,
                    Inputs = new Dictionary<string, object?> { ["cronExpression"] = "*/10 * * * *" }
                }
            },
            Steps = new StepCollection { ["step1"] = new StepMetadata { Type = "LogMessage" } }
        });

        var repository = Substitute.For<IFlowRepository>();
        repository.GetAllFlowsAsync().Returns(new List<IFlowDefinition> { flow });

        var store = Substitute.For<IFlowStore>();
        store.GetByIdAsync(flowId).Returns(new FlowDefinitionRecord { Id = flowId, IsEnabled = true });
        store.SaveAsync(Arg.Any<FlowDefinitionRecord>()).Returns(ci => ci.Arg<FlowDefinitionRecord>());

        _scheduleStateStore.GetAsync($"flow-{flowId}-scheduled")
            .Returns(new FlowScheduleState
            {
                JobId = $"flow-{flowId}-scheduled",
                IsPaused = false,
                CronOverride = "0 * * * *"
            });

        var sut = CreateSut(repository, store);

        // Act
        await sut.StartAsync(CancellationToken.None);

        // Assert
        _recurringJobManager.Received(1).AddOrUpdate(
            $"flow-{flowId}-scheduled",
            Arg.Any<global::Hangfire.Common.Job>(),
            "0 * * * *",
            Arg.Any<global::Hangfire.RecurringJobOptions>());
    }

    [Fact]
    public async Task StartAsync_InvalidGraph_Throws()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Version.Returns("1.0");
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["a"] = new StepMetadata { Type = "A", RunAfter = new RunAfterCollection { ["b"] = [StepStatus.Succeeded] } },
                ["b"] = new StepMetadata { Type = "B", RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] } }
            }
        });

        var repository = Substitute.For<IFlowRepository>();
        repository.GetAllFlowsAsync().Returns(new List<IFlowDefinition> { flow });

        var store = Substitute.For<IFlowStore>();

        var sut = CreateSut(repository, store);

        // Act
        var act = () => sut.StartAsync(CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(act);
    }
}
