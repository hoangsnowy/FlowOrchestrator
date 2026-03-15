using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using FluentAssertions;
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

    [Fact]
    public async Task StartAsync_SyncsFlowsToStore()
    {
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

        var sut = new FlowSyncHostedService(repository, store, _recurringJobManager, Logger);

        await sut.StartAsync(CancellationToken.None);

        await store.Received(1).SaveAsync(Arg.Is<FlowDefinitionRecord>(r =>
            r.Id == flow.Id && r.Version == "1.0" && r.ManifestJson != null));
    }

    [Fact]
    public async Task StartAsync_UpdatesExistingFlow()
    {
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Version.Returns("2.0");
        flow.Manifest.Returns(new FlowManifest());

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

        var sut = new FlowSyncHostedService(repository, store, _recurringJobManager, Logger);

        await sut.StartAsync(CancellationToken.None);

        await store.Received(1).SaveAsync(Arg.Is<FlowDefinitionRecord>(r =>
            r.Id == flowId && r.Version == "2.0"));
    }

    [Fact]
    public async Task StartAsync_FlowSyncFailure_DoesNotThrow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest());

        var repository = Substitute.For<IFlowRepository>();
        repository.GetAllFlowsAsync().Returns(new List<IFlowDefinition> { flow });

        var store = Substitute.For<IFlowStore>();
        store.GetByIdAsync(Arg.Any<Guid>()).Returns(Task.FromException<FlowDefinitionRecord?>(new Exception("DB error")));

        var sut = new FlowSyncHostedService(repository, store, _recurringJobManager, Logger);

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StopAsync_CompletesImmediately()
    {
        var repository = Substitute.For<IFlowRepository>();
        var store = Substitute.For<IFlowStore>();
        var sut = new FlowSyncHostedService(repository, store, _recurringJobManager, Logger);

        var act = () => sut.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_RegistersRecurringJobForCronTrigger()
    {
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
                    Type = "Cron",
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

        var sut = new FlowSyncHostedService(repository, store, _recurringJobManager, Logger);

        await sut.StartAsync(CancellationToken.None);

        _recurringJobManager.Received(1).AddOrUpdate(
            $"flow-{flowId}-scheduled",
            Arg.Any<global::Hangfire.Common.Job>(),
            "*/10 * * * *",
            Arg.Any<global::Hangfire.RecurringJobOptions>());
    }

    [Fact]
    public async Task StartAsync_RemovesRecurringJobForDisabledFlow()
    {
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
                    Type = "Cron",
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

        var sut = new FlowSyncHostedService(repository, store, _recurringJobManager, Logger);

        await sut.StartAsync(CancellationToken.None);

        _recurringJobManager.Received(1).RemoveIfExists($"flow-{flowId}-scheduled");
    }
}
