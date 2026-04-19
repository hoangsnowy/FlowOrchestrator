namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Data cleanup contract called periodically by <c>FlowRetentionHostedService</c>
/// to remove runs, steps, outputs, and events older than the configured TTL.
/// </summary>
public interface IFlowRetentionStore
{
    /// <summary>
    /// Deletes all run data (runs, steps, attempts, outputs, events) whose completion time
    /// is older than <paramref name="cutoffUtc"/>.
    /// </summary>
    /// <param name="cutoffUtc">Runs completed before this timestamp are eligible for deletion.</param>
    /// <param name="cancellationToken">Propagates cancellation from the host shutdown signal.</param>
    Task CleanupAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);
}
