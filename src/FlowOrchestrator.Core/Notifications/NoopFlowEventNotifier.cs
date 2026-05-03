namespace FlowOrchestrator.Core.Notifications;

/// <summary>
/// Default <see cref="IFlowEventNotifier"/> implementation that discards every event.
/// Registered when no realtime consumer (e.g. the dashboard's SSE broadcaster) is wired in,
/// so apps that never opt in pay zero overhead — <see cref="PublishAsync"/> returns a
/// completed <see cref="ValueTask"/> with no allocation.
/// </summary>
public sealed class NoopFlowEventNotifier : IFlowEventNotifier
{
    /// <summary>Shared singleton instance used as the engine's fallback when DI registration is absent.</summary>
    public static readonly NoopFlowEventNotifier Instance = new();

    /// <inheritdoc/>
    public ValueTask PublishAsync(FlowLifecycleEvent evt, CancellationToken ct = default) => ValueTask.CompletedTask;
}
