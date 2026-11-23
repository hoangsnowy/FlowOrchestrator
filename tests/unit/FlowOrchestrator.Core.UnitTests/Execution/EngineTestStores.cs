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
        return store;
    }

    /// <summary>Creates a plain run-control store substitute: no control record, no cancellation.</summary>
    /// <returns>An unconfigured <see cref="IFlowRunControlStore"/> substitute.</returns>
    public static IFlowRunControlStore ControlStore() => Substitute.For<IFlowRunControlStore>();
}
