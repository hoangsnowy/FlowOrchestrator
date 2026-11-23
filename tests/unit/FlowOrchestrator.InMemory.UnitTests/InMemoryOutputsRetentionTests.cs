using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// Retention of <see cref="InMemoryOutputsRepository"/>: trigger data, trigger headers, step
/// inputs/outputs of a run purged by the retention sweep must go with the run, or a
/// long-lived in-memory host grows by one run's payloads for every run it ever executed.
/// </summary>
public sealed class InMemoryOutputsRetentionTests
{
    private static readonly IFlowDefinition _flow = CreateFlow();

    private static IFlowDefinition CreateFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest());
        return flow;
    }

    private static async Task SeedOutputsAsync(IOutputsRepository outputs, Guid runId)
    {
        var trigger = new Trigger(
            "manual",
            "Manual",
            new { orderId = 1 },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Request-Id"] = "r-1" });
        var triggerCtx = new TriggerContext { RunId = runId, Flow = _flow, Trigger = trigger };
        var execCtx = new Core.Execution.ExecutionContext { RunId = runId };
        var step = new StepInstance("a", "Work") { RunId = runId, Inputs = new Dictionary<string, object?> { ["x"] = 1 } };

        await outputs.SaveTriggerDataAsync(triggerCtx, _flow, trigger);
        await outputs.SaveTriggerHeadersAsync(triggerCtx, _flow, trigger);
        await outputs.SaveStepInputAsync(execCtx, _flow, step);
        await outputs.SaveStepOutputAsync(execCtx, _flow, step, new StepResult { Key = "a", Result = new { ok = true } });
    }

    private static async Task AssertOutputsGoneAsync(InMemoryOutputsRepository outputs, Guid runId)
    {
        Assert.Null(await outputs.GetTriggerDataAsync(runId));
        Assert.Null(await outputs.GetTriggerHeadersAsync(runId));
        Assert.Null(await outputs.GetStepOutputAsync(runId, "a"));
        Assert.Null(await outputs.GetStepOutputAsync(runId, "a:input"));
    }

    private static async Task AssertOutputsPresentAsync(InMemoryOutputsRepository outputs, Guid runId)
    {
        Assert.NotNull(await outputs.GetTriggerDataAsync(runId));
        Assert.NotNull(await outputs.GetTriggerHeadersAsync(runId));
        Assert.NotNull(await outputs.GetStepOutputAsync(runId, "a"));
        Assert.NotNull(await outputs.GetStepOutputAsync(runId, "a:input"));
    }

    [Fact]
    public async Task Retention_sweep_over_UseInMemory_registrations_drops_outputs_of_purged_runs()
    {
        // Arrange — wire storage exactly as UseInMemory() does, then seed one expired run and one
        // run still inside the retention window, each with the full set of output kinds.
        var services = new ServiceCollection();
        new FlowOrchestratorBuilder(services).UseInMemory();
        using var provider = services.BuildServiceProvider();
        var runs = provider.GetRequiredService<InMemoryFlowRunStore>();
        var outputs = provider.GetRequiredService<InMemoryOutputsRepository>();

        var expired = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        await runs.StartRunAsync(_flow.Id, "Flow", expired, "manual", null, null);
        await runs.CompleteRunAsync(expired, "Succeeded");
        runs.TryMutateRunForTests(expired, r => r.CompletedAt = DateTimeOffset.UtcNow.AddDays(-30));
        await runs.StartRunAsync(_flow.Id, "Flow", fresh, "manual", null, null);
        await runs.CompleteRunAsync(fresh, "Succeeded");
        await SeedOutputsAsync(outputs, expired);
        await SeedOutputsAsync(outputs, fresh);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);

        // Act — the same loop FlowRetentionHostedService runs: every registered retention store, in
        // registration order.
        foreach (var store in provider.GetServices<IFlowRetentionStore>())
        {
            await store.CleanupAsync(cutoff, CancellationToken.None);
        }

        // Assert
        Assert.Null(await runs.GetRunAsync(expired));
        await AssertOutputsGoneAsync(outputs, expired);
        await AssertOutputsPresentAsync(outputs, fresh);
    }

    [Fact]
    public async Task Cleanup_keeps_outputs_of_a_run_that_is_still_running()
    {
        // Arrange — an in-flight run has no CompletedAt, so the run store keeps it and so must we.
        var runs = new InMemoryFlowRunStore();
        var outputs = new InMemoryOutputsRepository(runs);
        var running = Guid.NewGuid();
        await runs.StartRunAsync(_flow.Id, "Flow", running, "manual", null, null);
        await SeedOutputsAsync(outputs, running);
        var cutoff = DateTimeOffset.UtcNow.AddYears(1);

        // Act
        await runs.CleanupAsync(cutoff, CancellationToken.None);
        await outputs.CleanupAsync(cutoff, CancellationToken.None);

        // Assert
        await AssertOutputsPresentAsync(outputs, running);
    }

    [Fact]
    public async Task Standalone_repository_cleanup_is_a_no_op()
    {
        // Arrange — no paired run store, so there is no way to know which runs are gone.
        var outputs = new InMemoryOutputsRepository();
        var runId = Guid.NewGuid();
        await SeedOutputsAsync(outputs, runId);

        // Act
        await outputs.CleanupAsync(DateTimeOffset.UtcNow.AddYears(1), CancellationToken.None);

        // Assert
        await AssertOutputsPresentAsync(outputs, runId);
    }
}
