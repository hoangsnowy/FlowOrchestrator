using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Testing.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowOrchestrator.Testing;

/// <summary>
/// Running test host for a single flow type. Created by <see cref="FlowTestHostBuilder{TFlow}.BuildAsync"/>
/// and disposed by the test (preferably via <c>await using</c>).
/// </summary>
/// <typeparam name="TFlow">The flow under test.</typeparam>
public sealed class FlowTestHost<TFlow> : IAsyncDisposable where TFlow : class, IFlowDefinition, new()
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly IHost _host;
    private readonly Internal.FrozenTimeProvider? _frozenTimeProvider;
    private bool _disposed;

    internal FlowTestHost(IHost host, Internal.FrozenTimeProvider? frozenTimeProvider)
    {
        _host = host;
        _frozenTimeProvider = frozenTimeProvider;
    }

    /// <summary>The host's root service provider — escape hatch for advanced assertions.</summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>
    /// Starts a new run for the configured flow and waits for it to reach a terminal state
    /// (or for <paramref name="timeout"/> to elapse).
    /// </summary>
    /// <param name="triggerKey">Manifest trigger key (defaults to <c>"manual"</c>).</param>
    /// <param name="body">Trigger body — accessible via <c>@triggerBody()</c> expressions.</param>
    /// <param name="headers">Trigger headers — accessible via <c>@triggerHeaders()</c> expressions.</param>
    /// <param name="timeout">Maximum wall-clock time to wait. Defaults to 30 seconds.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A <see cref="FlowTestRunResult"/> snapshot at the time the wait ended.</returns>
    public Task<FlowTestRunResult> TriggerAsync(
        string triggerKey = "manual",
        object? body = null,
        IDictionary<string, string>? headers = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        TriggerInternalAsync(triggerKey, triggerType: "Manual", body, headers, timeout, cancellationToken);

    /// <summary>
    /// Convenience wrapper around <see cref="TriggerAsync"/> for webhook-typed triggers.
    /// Sends the trigger with type <c>"Webhook"</c> and trigger key <paramref name="slug"/>.
    /// </summary>
    public Task<FlowTestRunResult> TriggerWebhookAsync(
        string slug,
        object? body = null,
        IDictionary<string, string>? headers = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        TriggerInternalAsync(slug, triggerType: "Webhook", body, headers, timeout, cancellationToken);

    /// <summary>
    /// Advances the frozen clock supplied via <see cref="FlowTestHostBuilder{TFlow}.WithSystemClock"/>
    /// by <paramref name="duration"/>. No-op if no frozen clock was configured.
    /// </summary>
    public Task FastForwardAsync(TimeSpan duration)
    {
        ThrowIfDisposed();
        _frozenTimeProvider?.Advance(duration);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Polls the run store for an existing run and returns when it reaches a terminal status
    /// or <paramref name="timeout"/> elapses. Useful for waiting on cron-triggered runs.
    /// </summary>
    public Task<FlowTestRunResult> WaitForRunAsync(Guid runId, TimeSpan timeout)
    {
        ThrowIfDisposed();
        var runStore = Services.GetRequiredService<IFlowRunStore>();
        var eventReader = Services.GetService<IFlowEventReader>();
        return RunPoller.WaitForTerminalAsync(runStore, eventReader, runId, timeout, CancellationToken.None);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Best-effort shutdown: catch every step so disposal always completes —
        // partner libraries may dispose CTSs idempotently or otherwise.
        try
        {
            if (_host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                _host.Dispose();
            }
        }
        catch
        {
            // Swallow — prefer disposal completion over surfacing teardown errors in tests.
        }
    }

    private async Task<FlowTestRunResult> TriggerInternalAsync(
        string triggerKey,
        string triggerType,
        object? body,
        IDictionary<string, string>? headers,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        using var scope = Services.CreateScope();
        var sp = scope.ServiceProvider;

        var orchestrator = sp.GetRequiredService<IFlowOrchestrator>();
        var runStore = sp.GetRequiredService<IFlowRunStore>();
        var eventReader = sp.GetService<IFlowEventReader>();
        var flow = sp.GetServices<IFlowDefinition>().OfType<TFlow>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Flow {typeof(TFlow).Name} is not registered. Ensure AddFlow<{typeof(TFlow).Name}>() ran in the host configuration.");

        IReadOnlyDictionary<string, string>? triggerHeaders = headers is null
            ? null
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);

        var triggerContext = new TriggerContext
        {
            Flow = flow,
            Trigger = new Trigger(triggerKey, triggerType, body, triggerHeaders),
            RunId = Guid.Empty,
            TriggerData = body,
            TriggerHeaders = triggerHeaders
        };

        await orchestrator.TriggerAsync(triggerContext, cancellationToken).ConfigureAwait(false);

        var runId = triggerContext.RunId;
        return await RunPoller
            .WaitForTerminalAsync(runStore, eventReader, runId, timeout ?? DefaultTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FlowTestHost<TFlow>));
    }
}
