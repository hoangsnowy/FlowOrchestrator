using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Shared store doubles for unit tests that construct <see cref="FlowOrchestratorEngine"/>,
/// <see cref="FlowSignalDispatcher"/> or <c>FlowRunRecoveryHostedService</c> directly.
/// </summary>
/// <remarks>
/// Since v2.0 those components require an <see cref="IFlowRunRuntimeStore"/> (and the engine an
/// <see cref="IFlowRunControlStore"/>) and fail fast at construction without one. Tests that do not
/// care about claim or run-control behaviour use these permissive doubles so their assertions keep
/// targeting the behaviour under test rather than storage wiring.
/// </remarks>
internal static class EngineTestStores
{
    /// <summary>
    /// Creates a runtime-store substitute that grants every claim, reports no claimed keys, no run
    /// status, and the supplied step statuses (empty when <paramref name="statuses"/> is
    /// <see langword="null"/>).
    /// </summary>
    /// <param name="statuses">Step statuses the scenario implies; returned for every run id.</param>
    /// <returns>A configured <see cref="IFlowRunRuntimeStore"/> substitute.</returns>
    public static IFlowRunRuntimeStore PermissiveRuntimeStore(
        IReadOnlyDictionary<string, StepStatus>? statuses = null)
    {
        var store = Substitute.For<IFlowRunRuntimeStore>();
        store.TryClaimStepAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns(Task.FromResult(true));
        store.GetClaimedStepKeysAsync(Arg.Any<Guid>())
            .Returns(Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>()));
        store.GetStepStatusesAsync(Arg.Any<Guid>())
            .Returns(Task.FromResult(statuses ?? new Dictionary<string, StepStatus>(StringComparer.Ordinal)));
        store.GetRunStatusAsync(Arg.Any<Guid>())
            .Returns(Task.FromResult<string?>(null));
        DeriveKeyedReads(store, () => statuses);
        return store;
    }

    /// <summary>
    /// Answers the keyed status reads of a runtime-store substitute from the same status map the scenario
    /// supplies for the full read, so the engine's frontier evaluation, completion gate and loop barrier see
    /// one consistent run.
    /// </summary>
    /// <param name="store">The substitute to configure.</param>
    /// <param name="statuses">Supplies the run's status map at call time; <see langword="null"/> means no rows.</param>
    /// <remarks>
    /// <c>GetStepStatusesAsync(runId, keys)</c> filters the map to the requested keys,
    /// <c>HasRunningOrPendingStepAsync</c> scans it for an open row, and
    /// <c>CountSettledIterationsAsync</c> counts iterations whose body rows are all terminal — the contract
    /// every real store implements.
    /// </remarks>
    public static void DeriveKeyedReads(IFlowRunRuntimeStore store, Func<IReadOnlyDictionary<string, StepStatus>?> statuses)
    {
        store.GetStepStatusesAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(call =>
            {
                var all = statuses() ?? new Dictionary<string, StepStatus>();
                var keyed = call.Arg<IReadOnlyCollection<string>>()
                    .Where(all.ContainsKey)
                    .Distinct(StringComparer.Ordinal)
                    .ToDictionary(key => key, key => all[key], StringComparer.Ordinal);

                return Task.FromResult<IReadOnlyDictionary<string, StepStatus>>(keyed);
            });
        store.HasRunningOrPendingStepAsync(Arg.Any<Guid>())
            .Returns(_ => Task.FromResult(
                (statuses() ?? new Dictionary<string, StepStatus>()).Values.Any(s => s is StepStatus.Running or StepStatus.Pending)));
        store.CountSettledIterationsAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(call =>
            {
                var all = statuses() ?? new Dictionary<string, StepStatus>();
                var loop = call.ArgAt<string>(1);
                var iterations = call.ArgAt<int>(2);
                var body = call.ArgAt<IReadOnlyCollection<string>>(3);
                var settled = 0;
                for (var index = 0; index < iterations; index++)
                {
                    if (body.All(child => all.TryGetValue($"{loop}.{index}.{child}", out var s)
                        && s is StepStatus.Succeeded or StepStatus.Failed or StepStatus.Skipped))
                    {
                        settled++;
                    }
                }

                return Task.FromResult(settled);
            });
    }

    /// <summary>Creates a plain run-control store substitute: no control record, no cancellation.</summary>
    /// <returns>An unconfigured <see cref="IFlowRunControlStore"/> substitute.</returns>
    public static IFlowRunControlStore ControlStore() => Substitute.For<IFlowRunControlStore>();
}
