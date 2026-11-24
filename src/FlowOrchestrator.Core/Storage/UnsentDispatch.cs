namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// A dispatch-ledger row whose hand-off to the runtime never completed, leased to the caller by
/// <see cref="IFlowRunStore.LeaseUnsentDispatchesAsync"/> so it can be re-delivered.
/// </summary>
/// <param name="RunId">The run the step belongs to.</param>
/// <param name="FlowId">The flow the run executes; used to resolve the definition from the registry.</param>
/// <param name="StepKey">The runtime step key holding the ledger row, e.g. <c>"loop.3.child"</c>.</param>
public sealed record UnsentDispatch(Guid RunId, Guid FlowId, string StepKey);
