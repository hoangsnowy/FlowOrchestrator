namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Proactively enforces run-level timeout deadlines. Implemented by <see cref="FlowOrchestratorEngine"/>
/// and driven by the periodic <c>FlowTimeoutEnforcementHostedService</c>.
/// </summary>
/// <remarks>
/// Separated from <see cref="IFlowOrchestrator"/> so the hosted service can depend on just the
/// enforcement entry point without coupling to the full orchestration surface, and so runtime shims
/// are unaffected.
/// </remarks>
public interface IRunTimeoutEnforcer
{
    /// <summary>
    /// Scans every active run and marks those whose timeout deadline has already passed as
    /// <c>TimedOut</c>, completing the run when no step is still in flight.
    /// </summary>
    /// <param name="cancellationToken">Token that aborts the sweep between runs.</param>
    /// <remarks>
    /// A no-op when no <see cref="Storage.IFlowRunControlStore"/> is registered. Runs with an
    /// in-flight step are latched <c>TimedOut</c> but not force-completed — the in-flight guard in
    /// run completion converges the run once the step finishes.
    /// </remarks>
    Task EnforceDueTimeoutsAsync(CancellationToken cancellationToken = default);
}
