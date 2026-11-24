namespace FlowOrchestrator.Core.Execution.Internal;

/// <summary>
/// Values the engine writes into a dispatch-ledger row's job-id column that are not runtime ids.
/// </summary>
internal static class DispatchLedgerMarker
{
    /// <summary>
    /// Recorded when the runtime accepted a step but returned no job/message id.
    /// </summary>
    /// <remarks>
    /// A ledger row without a job id reads as a hand-off that never returned, and the unsent-dispatch sweep
    /// re-delivers it. A runtime that legitimately produces no id would otherwise see every step it has not
    /// started within <c>UnsentDispatchTimeout</c> delivered again, once per window.
    /// </remarks>
    public const string AcceptedWithoutJobId = "(accepted, no job id)";

    /// <summary>Returns <paramref name="jobId"/>, or <see cref="AcceptedWithoutJobId"/> when the runtime gave none.</summary>
    /// <param name="jobId">The id the runtime returned for an accepted hand-off.</param>
    /// <returns>The value to annotate the ledger row with.</returns>
    public static string For(string? jobId) => jobId ?? AcceptedWithoutJobId;
}
