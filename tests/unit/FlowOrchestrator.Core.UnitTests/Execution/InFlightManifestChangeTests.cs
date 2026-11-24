using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;
using static FlowOrchestrator.Core.Tests.Execution.ManifestDeployHarness;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// What an in-flight run does when a deploy changes its flow's manifest: the same change must produce the
/// same outcome whichever code path meets the new manifest first — the continuation of the next completing
/// step, startup recovery of a restarted host, a rolling deploy in which a not-yet-restarted replica is handed
/// a step of the new version, or an in-process runtime that lost its queue in the restart.
/// </summary>
/// <remarks>
/// <para>
/// The rule these tests lock down is <b>latest manifest wins</b>: from the moment a worker evaluates the run
/// against the new manifest, every step of that manifest whose <c>runAfter</c> prerequisites are satisfied by the
/// run's recorded statuses is ready and runs — including a step added after its prerequisites had already
/// completed, and a new entry step. Steps that are no longer in the manifest are not dispatched again; one that
/// was already queued is recorded <see cref="StepStatus.Skipped"/> by a worker holding the registered manifest.
/// </para>
/// <para>
/// Every run is parked at a signal-gated step <c>wait</c> of the v1 shape <c>s1 → wait → s3 → s4</c>, the
/// shape of the stress harness's <c>deploy-mid-run</c> scenario, then the deploy happens, then the signal.
/// </para>
/// </remarks>
public sealed class InFlightManifestChangeTests
{
    /// <summary>How the deploy reaches the parked run.</summary>
    public enum DeployPath
    {
        /// <summary>The single replica restarts without recovery: only the continuation of <c>wait</c> sees v2.</summary>
        Continuation,

        /// <summary>The single replica restarts on a durable runtime and startup recovery evaluates the run under v2.</summary>
        Recovery,

        /// <summary>
        /// Two replicas sharing one durable queue are restarted one at a time. The v2 replica's recovery dispatches
        /// the newly-ready steps and the still-v1 replica is handed them first — the Hangfire stress run.
        /// </summary>
        RollingDeploy,

        /// <summary>The in-process runtime restarts: its queue is gone and recovery redelivers the ledgered steps.</summary>
        InProcessRestart
    }

    private static readonly Guid FlowId = new("5f0e0000-0000-4000-8099-000000000001");

    private static IFlowDefinition V1() => Flow(FlowId, "1", new StepCollection
    {
        ["s1"] = Work(),
        ["wait"] = Gate("s1"),
        ["s3"] = Work("wait"),
        ["s4"] = Work("s3")
    });

    private static IFlowDefinition V2(string variant) => Flow(FlowId, "2", variant switch
    {
        "append" => new StepCollection
        {
            ["s1"] = Work(), ["wait"] = Gate("s1"), ["s3"] = Work("wait"), ["s4"] = Work("s3"), ["s5"] = Work("s4")
        },
        "insert-middle" => new StepCollection
        {
            ["s1"] = Work(), ["wait"] = Gate("s1"), ["mid"] = Work("wait"), ["s3"] = Work("mid"), ["s4"] = Work("s3")
        },
        "rename" => new StepCollection
        {
            ["s1"] = Work(), ["wait"] = Gate("s1"), ["s3b"] = Work("wait"), ["s4"] = Work("s3b")
        },
        "remove" => new StepCollection
        {
            ["s1"] = Work(), ["wait"] = Gate("s1"), ["s4"] = Work("wait")
        },
        "new-runafter" => new StepCollection
        {
            ["s1"] = Work(), ["wait"] = Gate("s1"), ["s3"] = Work("wait"), ["side"] = Work("s1"), ["s4"] = Work("s3", "side")
        },
        "new-entry" => new StepCollection
        {
            ["e0"] = Work(), ["s1"] = Work(), ["wait"] = Gate("s1"), ["s3"] = Work("wait"), ["s4"] = Work("s3")
        },
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null)
    });

    /// <summary>The step keys each variant must end with, all Succeeded, on every path.</summary>
    private static string[] ExpectedSucceeded(string variant) => variant switch
    {
        "append" => ["s1", "s3", "s4", "s5", "wait"],
        "insert-middle" => ["mid", "s1", "s3", "s4", "wait"],
        "rename" => ["s1", "s3b", "s4", "wait"],
        "remove" => ["s1", "s4", "wait"],
        "new-runafter" => ["s1", "s3", "s4", "side", "wait"],
        "new-entry" => ["e0", "s1", "s3", "s4", "wait"],
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null)
    };

    /// <summary>Every variant of the stress scenario, on every deploy path.</summary>
    public static TheoryData<string, DeployPath> Matrix()
    {
        var data = new TheoryData<string, DeployPath>();
        foreach (var variant in new[] { "append", "insert-middle", "rename", "remove", "new-runafter", "new-entry" })
        {
            foreach (var path in Enum.GetValues<DeployPath>())
            {
                data.Add(variant, path);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task ParkedRun_ManifestChangedByDeploy_LatestManifestWinsOnEveryPath(string variant, DeployPath path)
    {
        // Arrange
        var harness = new ManifestDeployHarness(losesQueuedStepsOnRestart: path == DeployPath.InProcessRestart);
        var runId = await ParkOnV1Async(harness, replicas: path == DeployPath.RollingDeploy ? 2 : 1);
        var v2 = V2(variant);

        // Act
        switch (path)
        {
            case DeployPath.Continuation:
                await harness.StartReplicaAsync(0, v2, recover: false);
                break;

            case DeployPath.Recovery:
            case DeployPath.InProcessRestart:
                await harness.StartReplicaAsync(0, v2);
                break;

            case DeployPath.RollingDeploy:
                // Replica 0 restarts on v2; its recovery dispatches whatever v2 makes ready. Replica 1 has not
                // restarted yet and is the one that dequeues those jobs.
                await harness.StartReplicaAsync(0, v2);
                await harness.RunQueuedOnceOnAsync(replica: 1);
                await harness.StartReplicaAsync(1, v2);
                break;
        }

        harness.Signal();
        await harness.DrainOnAsync(replica: 0);

        // Assert
        var statuses = await harness.StepStatusesAsync(runId);
        Assert.Equal(ExpectedSucceeded(variant), statuses.Keys.Order(StringComparer.Ordinal));
        Assert.All(statuses, s => Assert.Equal(nameof(StepStatus.Succeeded), s.Value));
        Assert.Equal(nameof(StepStatus.Succeeded), await harness.RunStatusAsync(runId));
    }

    [Theory]
    [InlineData("new-runafter", "side")]
    [InlineData("new-entry", "e0")]
    public async Task RollingDeploy_StaleReplicaHandedNewStep_HandsItBackWithoutDecidingIt(string variant, string newStep)
    {
        // Arrange
        var harness = new ManifestDeployHarness();
        var runId = await ParkOnV1Async(harness, replicas: 2);
        await harness.StartReplicaAsync(0, V2(variant));
        Assert.Contains(newStep, harness.QueuedKeys);

        // Act
        await harness.RunQueuedOnceOnAsync(replica: 1);

        // Assert
        Assert.Null((await harness.StepStatusesAsync(runId)).GetValueOrDefault(newStep));
        Assert.False(await harness.Store.IsStepClaimedAsync(runId, newStep));
        Assert.True(await harness.Store.IsStepDispatchedAsync(runId, newStep));
        Assert.Contains(newStep, harness.QueuedKeys);
        Assert.DoesNotContain(harness.Executions, e => e.StepKey == newStep);
        Assert.Equal("Running", await harness.RunStatusAsync(runId));
    }

    [Theory]
    [InlineData("remove", false)]
    [InlineData("remove", true)]
    [InlineData("rename", false)]
    [InlineData("rename", true)]
    public async Task StepQueuedUnderV1_RemovedByDeploy_IsSkippedAndRunCompletes(string variant, bool inProcessRestart)
    {
        // Arrange
        var harness = new ManifestDeployHarness(losesQueuedStepsOnRestart: inProcessRestart);
        var runId = await ParkOnV1Async(harness, replicas: 1);
        harness.Signal();
        await harness.RunKeyOnAsync(0, "wait");
        Assert.Equal(["s3"], harness.QueuedKeys);

        // Act
        await harness.StartReplicaAsync(0, V2(variant));
        await harness.DrainOnAsync(replica: 0);

        // Assert
        var statuses = await harness.StepStatusesAsync(runId);
        Assert.Equal(nameof(StepStatus.Skipped), statuses["s3"]);
        Assert.Equal("Step metadata not found.", await harness.StepReasonAsync(runId, "s3"));
        Assert.Equal(nameof(StepStatus.Succeeded), statuses["s4"]);
        Assert.DoesNotContain(harness.Executions, e => e.StepKey == "s3");
        Assert.Equal(nameof(StepStatus.Succeeded), await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task UnknownStep_NoManifestRegistered_IsSkippedAsBefore()
    {
        // Arrange
        var harness = new ManifestDeployHarness();
        var runId = await ParkOnV1Async(harness, replicas: 1);
        await harness.StartReplicaAsync(0, V2("new-entry"));
        await harness.StartReplicaAsync(1, V1(), recover: false);
        await harness.FlowStore.DeleteAsync(FlowId);

        // Act
        await harness.RunKeyOnAsync(1, "e0");

        // Assert
        Assert.Equal(nameof(StepStatus.Skipped), (await harness.StepStatusesAsync(runId))["e0"]);
        Assert.Equal("Step metadata not found.", await harness.StepReasonAsync(runId, "e0"));
    }

    [Fact]
    public async Task UnknownStep_RegisteredManifestUnreadable_IsHandedBackNotSkipped()
    {
        // Arrange
        var harness = new ManifestDeployHarness();
        var runId = await ParkOnV1Async(harness, replicas: 1);
        await harness.StartReplicaAsync(0, V2("new-entry"));
        await harness.StartReplicaAsync(1, V1(), recover: false);
        var record = await harness.FlowStore.GetByIdAsync(FlowId);
        record!.ManifestJson = "{ not json";
        await harness.FlowStore.SaveAsync(record);

        // Act
        await harness.RunKeyOnAsync(1, "e0");

        // Assert
        Assert.Null((await harness.StepStatusesAsync(runId)).GetValueOrDefault("e0"));
        Assert.Contains("e0", harness.QueuedKeys);
    }

    [Fact]
    public async Task UnknownStep_RegisteredManifestReadThrows_IsHandedBackNotSkipped()
    {
        // Arrange
        FailingFlowStore? failing = null;
        var harness = new ManifestDeployHarness(engineFlowStore: inner => failing = new FailingFlowStore(inner));
        var runId = await ParkOnV1Async(harness, replicas: 1);
        await harness.StartReplicaAsync(0, V2("new-entry"));
        await harness.StartReplicaAsync(1, V1(), recover: false);
        failing!.Fail = true;

        // Act
        await harness.RunKeyOnAsync(1, "e0");

        // Assert
        Assert.Null((await harness.StepStatusesAsync(runId)).GetValueOrDefault("e0"));
        Assert.False(await harness.Store.IsStepClaimedAsync(runId, "e0"));
        Assert.Contains("e0", harness.QueuedKeys);
    }

    [Fact]
    public async Task RollingDeploy_RolledBackToV1_NewStepIsSkippedOnceStoreAgrees()
    {
        // Arrange
        var harness = new ManifestDeployHarness();
        var runId = await ParkOnV1Async(harness, replicas: 2);
        await harness.StartReplicaAsync(0, V2("new-entry"));
        await harness.RunQueuedOnceOnAsync(replica: 1);
        Assert.Contains("e0", harness.QueuedKeys);

        // Act — the deploy is rolled back: replica 0 restarts on v1 and re-syncs v1 to the flow store.
        await harness.StartReplicaAsync(0, V1());
        harness.Signal();
        await harness.DrainOnAsync(replica: 1);

        // Assert
        var statuses = await harness.StepStatusesAsync(runId);
        Assert.Equal(nameof(StepStatus.Skipped), statuses["e0"]);
        Assert.Equal(nameof(StepStatus.Succeeded), statuses["s4"]);
        Assert.Equal(nameof(StepStatus.Succeeded), await harness.RunStatusAsync(runId));
    }

    /// <summary>
    /// Starts <paramref name="replicas"/> replicas on v1, triggers one run, and executes it until it is parked at
    /// <c>wait</c> (Pending, re-queued).
    /// </summary>
    private static async Task<Guid> ParkOnV1Async(ManifestDeployHarness harness, int replicas)
    {
        for (var r = 0; r < replicas; r++)
        {
            await harness.StartReplicaAsync(r, V1());
        }

        var runId = await harness.TriggerAsync(0);
        await harness.RunKeyOnAsync(0, "s1");
        await harness.RunKeyOnAsync(0, "wait");
        Assert.Equal(["wait"], harness.QueuedKeys);
        Assert.Equal(nameof(StepStatus.Pending), (await harness.StepStatusesAsync(runId))["wait"]);
        return runId;
    }

    /// <summary>Flow store whose <see cref="IFlowStore.GetByIdAsync"/> throws once <see cref="Fail"/> is set.</summary>
    private sealed class FailingFlowStore(IFlowStore inner) : IFlowStore
    {
        /// <summary>When set, reads of a single definition throw.</summary>
        public volatile bool Fail;

        public Task<IReadOnlyList<FlowDefinitionRecord>> GetAllAsync() => inner.GetAllAsync();

        public Task<FlowDefinitionRecord?> GetByIdAsync(Guid id)
            => Fail ? Task.FromException<FlowDefinitionRecord?>(new InvalidOperationException("flow store down")) : inner.GetByIdAsync(id);

        public Task<FlowDefinitionRecord> SaveAsync(FlowDefinitionRecord record) => inner.SaveAsync(record);

        public Task DeleteAsync(Guid id) => inner.DeleteAsync(id);

        public Task<FlowDefinitionRecord> SetEnabledAsync(Guid id, bool enabled) => inner.SetEnabledAsync(id, enabled);
    }
}
