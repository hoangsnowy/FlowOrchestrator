using System.Collections.Concurrent;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using FlowOrchestrator.PostgreSQL;
using FlowOrchestrator.SqlServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.StorageMigration.IntegrationTests;

/// <summary>
/// A real FlowOrchestrator 2.0 host (engine + in-memory runtime) on a real SQL Server or PostgreSQL store,
/// used to produce the source data and to prove the target works after the copy. Waits are logical: a run's
/// <see cref="RunCompletedEvent"/>, a waiter's registration — never a poll against the clock.
/// </summary>
public sealed class FlowHost : IAsyncDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);
    private readonly IHost _host;

    private FlowHost(IHost host)
    {
        _host = host;
    }

    public IServiceProvider Services => _host.Services;

    public RunEvents Events => Services.GetRequiredService<RunEvents>();

    public Gates Gates => Services.GetRequiredService<Gates>();

    public WaiterProbe Waiters => Services.GetRequiredService<WaiterProbe>();

    public static async Task<FlowHost> StartAsync(FlowStorageProvider provider, string connectionString, Action<FlowOrchestratorBuilder>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<RunEvents>();
        builder.Services.AddSingleton<IFlowEventNotifier>(sp => sp.GetRequiredService<RunEvents>());
        builder.Services.AddSingleton<Gates>();
        builder.Services.AddSingleton<WaiterProbe>();
        builder.Services.AddFlowOrchestrator(o =>
        {
            if (provider == FlowStorageProvider.SqlServer)
            {
                o.UseSqlServer(connectionString);
                o.AddSqlServerWebhookHardening(connectionString);
            }
            else
            {
                o.UsePostgreSql(connectionString);
                o.AddPostgreSqlWebhookHardening(connectionString);
            }

            o.UseInMemoryRuntime();
            o.AddFlow<OrderFlow>();
            o.AddFlow<FailingFlow>();
            o.AddFlow<GateFlow>();
            o.AddFlow<ApprovalFlow>();
            o.AddFlow<NightlyFlow>();
            o.RunControl.DefaultRunTimeout = TimeSpan.FromDays(2);
            configure?.Invoke(o);
        });
        builder.Services.AddStepHandler<EchoHandler>("Echo");
        builder.Services.AddStepHandler<FailHandler>("Fail");
        builder.Services.AddStepHandler<GateHandler>("Gate");

        // Decorate the backend's signal store so a test can wait for "the waiter is registered".
        var signalStore = builder.Services.Last(d => d.ServiceType == typeof(IFlowSignalStore));
        builder.Services.Remove(signalStore);
        builder.Services.AddSingleton<IFlowSignalStore>(sp => new ProbingSignalStore(
            (IFlowSignalStore)signalStore.ImplementationFactory!(sp), sp.GetRequiredService<WaiterProbe>()));

        var host = builder.Build();
        await host.StartAsync();
        return new FlowHost(host);
    }

    /// <summary>Triggers <typeparamref name="TFlow"/> manually and returns the run id (the existing one for a duplicate).</summary>
    public async Task<Guid> TriggerAsync<TFlow>(object? body, IDictionary<string, string>? headers = null)
        where TFlow : IFlowDefinition
    {
        using var scope = Services.CreateScope();
        var flow = scope.ServiceProvider.GetServices<IFlowDefinition>().OfType<TFlow>().Single();
        IReadOnlyDictionary<string, string>? h = headers is null ? null : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        var context = new TriggerContext
        {
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", body, h),
            TriggerData = body,
            TriggerHeaders = h,
        };
        var result = await scope.ServiceProvider.GetRequiredService<IFlowOrchestrator>().TriggerAsync(context);
        return result is FlowTriggerResult { RunId: { } existing } ? existing : context.RunId;
    }

    /// <summary>Waits for the run's completion event and returns its terminal status.</summary>
    public Task<string> CompletedAsync(Guid runId) => Events.For(runId).WaitAsync(Budget);

    /// <summary>Triggers and waits for the terminal status.</summary>
    public async Task<(Guid RunId, string Status)> RunAsync<TFlow>(object? body, IDictionary<string, string>? headers = null)
        where TFlow : IFlowDefinition
    {
        var runId = await TriggerAsync<TFlow>(body, headers);
        return (runId, await CompletedAsync(runId));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

/// <summary>Completes a task per run when the engine publishes <see cref="RunCompletedEvent"/>.</summary>
public sealed class RunEvents : IFlowEventNotifier
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string>> _completed = new();

    public Task<string> For(Guid runId) => Get(runId).Task;

    /// <summary>Forgets an earlier completion and returns a task for the run's next one (call before the retry).</summary>
    public Task<string> Next(Guid runId)
    {
        var next = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completed[runId] = next;
        return next.Task;
    }

    public ValueTask PublishAsync(FlowLifecycleEvent evt, CancellationToken ct = default)
    {
        if (evt is RunCompletedEvent completed)
            Get(completed.RunId).TrySetResult(completed.Status);
        return ValueTask.CompletedTask;
    }

    private TaskCompletionSource<string> Get(Guid runId) =>
        _completed.GetOrAdd(runId, _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
}

/// <summary>Completes a task per run once a signal waiter is durably registered.</summary>
public sealed class WaiterProbe
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _registered = new();

    public Task Registered(Guid runId) => Get(runId).Task.WaitAsync(TimeSpan.FromSeconds(60));

    internal void OnRegistered(Guid runId) => Get(runId).TrySetResult();

    private TaskCompletionSource Get(Guid runId) =>
        _registered.GetOrAdd(runId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}

internal sealed class ProbingSignalStore : IFlowSignalStore
{
    private readonly IFlowSignalStore _inner;
    private readonly WaiterProbe _probe;

    public ProbingSignalStore(IFlowSignalStore inner, WaiterProbe probe)
    {
        _inner = inner;
        _probe = probe;
    }

    public async ValueTask RegisterWaiterAsync(Guid runId, string stepKey, string signalName, DateTimeOffset? expiresAt, CancellationToken ct = default)
    {
        await _inner.RegisterWaiterAsync(runId, stepKey, signalName, expiresAt, ct);
        _probe.OnRegistered(runId);
    }

    public ValueTask<SignalDeliveryResult> DeliverSignalAsync(Guid runId, string signalName, string payloadJson, CancellationToken ct = default) =>
        _inner.DeliverSignalAsync(runId, signalName, payloadJson, ct);

    public ValueTask<FlowSignalWaiter?> GetWaiterAsync(Guid runId, string stepKey, CancellationToken ct = default) =>
        _inner.GetWaiterAsync(runId, stepKey, ct);

    public ValueTask RemoveWaiterAsync(Guid runId, string stepKey, CancellationToken ct = default) =>
        _inner.RemoveWaiterAsync(runId, stepKey, ct);
}
