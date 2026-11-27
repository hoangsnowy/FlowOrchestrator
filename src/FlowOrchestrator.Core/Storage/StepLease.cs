namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// An execution claim on one step that this process holds for a bounded time, identified by its fence.
/// </summary>
/// <param name="RunId">The run owning the step.</param>
/// <param name="StepKey">The claimed step.</param>
/// <param name="Fence">
/// Monotonic token of this acquisition: <c>1</c> for the first claim of the step, raised by one every time an
/// expired lease is taken over. Every write made on the lease's behalf is accepted only while the stored fence
/// still equals this value, so a worker whose lease was taken over cannot overwrite the new owner's work.
/// </param>
/// <remarks>
/// <para>
/// A lease replaces the 1.x claim that never expired: a worker killed mid-handler left its claim for ever, every
/// redelivery of the step exited on "claimed by another worker", and the run stayed Running until an operator
/// intervened (RFC 2.0 C1). The lease expires unless the owning process renews it, so once its owner is gone
/// another delivery takes the step over (<see cref="IFlowRunRuntimeStore.TryAcquireStepLeaseAsync"/>).
/// </para>
/// <para>
/// Taking over is allowed only while the step has not reached a terminal status: a completed step keeps its
/// claim for good, which is what keeps execution once-only under at-least-once delivery.
/// </para>
/// </remarks>
public sealed record StepLease(Guid RunId, string StepKey, long Fence);

/// <summary>A step whose execution lease expired without a terminal status, selected for redelivery.</summary>
/// <param name="RunId">The run owning the step.</param>
/// <param name="FlowId">The run's flow, so the sweep can resolve the step's definition.</param>
/// <param name="StepKey">The step whose owner stopped renewing its lease.</param>
public sealed record ExpiredStepLease(Guid RunId, Guid FlowId, string StepKey);
