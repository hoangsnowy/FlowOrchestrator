using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.InMemory;

/// <summary>
/// Background service that drains the in-process step channel and executes each step
/// by calling <see cref="IFlowOrchestrator.RunStepAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// This service is the in-memory equivalent of a Hangfire worker thread.
/// Steps dispatched by <see cref="InMemoryStepDispatcher"/> are consumed here one at a time.
/// For parallelism, configure the channel with a bounded capacity and increase
/// the degree of parallelism via the <see cref="MaxConcurrency"/> property (default: 1).
/// </para>
/// <para>
/// On shutdown the service stops taking new steps, signals the stopping token to the steps already
/// running (as Hangfire signals its shutdown token to running jobs), and then waits for them to return
/// before <see cref="BackgroundService.ExecuteTask"/> completes — so their final status writes, claim
/// releases and reschedules finish before the host disposes the services they use. The wait is bounded by
/// the host's shutdown timeout (<c>HostOptions.ShutdownTimeout</c>), after which the host stops waiting.
/// </para>
/// </remarks>
internal sealed partial class InMemoryStepRunnerHostedService : BackgroundService
{
    private readonly ChannelReader<InMemoryStepEnvelope> _reader;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InMemoryStepRunnerHostedService> _logger;

    // Steps started and not yet returned. Keyed by the step's task and removed by its own continuation, so
    // the map holds at most MaxConcurrency entries: O(1) bookkeeping per step.
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private SemaphoreSlim? _semaphore;

    /// <summary>Maximum number of steps processed concurrently. Default is 1 (sequential).</summary>
    public int MaxConcurrency { get; init; } = 1;

    /// <summary>Initialises the service with required dependencies.</summary>
    public InMemoryStepRunnerHostedService(
        ChannelReader<InMemoryStepEnvelope> reader,
        IServiceScopeFactory scopeFactory,
        ILogger<InMemoryStepRunnerHostedService> logger)
    {
        _reader = reader;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _semaphore?.Dispose();
        base.Dispose();
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        var semaphore = _semaphore;

        try
        {
            await foreach (var envelope in _reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await semaphore.WaitAsync(stoppingToken).ConfigureAwait(false);

                // Run each step in a separate scope so scoped services (e.g. IExecutionContextAccessor) are isolated.
                var running = Task.Run(() => RunEnvelopeAsync(envelope, semaphore, stoppingToken), stoppingToken);
                _inFlight.TryAdd(running, 0);

                // Registered after the add, so a step that has already returned is removed straight away.
                _ = running.ContinueWith(
                    static (task, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(task, out _),
                    _inFlight,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            await DrainInFlightAsync().ConfigureAwait(false);
        }
    }

    private async Task RunEnvelopeAsync(InMemoryStepEnvelope envelope, SemaphoreSlim semaphore, CancellationToken stoppingToken)
    {
        // Re-establish the parent trace context captured at dispatch time so the
        // step's flow.step span becomes a child of the original caller's trace,
        // not an orphan root. Mirrors TraceContextHangfireFilter for the InMemory runtime.
        using var wrapperActivity = envelope.ParentTraceContext is { } parentCtx
            ? FlowOrchestratorTelemetry.SharedActivitySource.StartActivity(
                "flow.runtime.execute",
                ActivityKind.Consumer,
                parentCtx)
            : null;
        wrapperActivity?.SetTag("messaging.system", "in_memory_channel");
        wrapperActivity?.SetTag("messaging.operation", "process");
        wrapperActivity?.SetTag("messaging.message.id", envelope.EnvelopeId);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<IFlowOrchestrator>();
            await engine.RunStepAsync(envelope.Context, envelope.Flow, envelope.Step, stoppingToken)
                        .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(_logger, ex, envelope.Step.Key, envelope.Context.RunId);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Waits for every step still running to return. Runs once the channel loop has ended (shutdown); the
    /// host's shutdown timeout bounds how long <c>StopAsync</c> waits on it.
    /// </summary>
    private async Task DrainInFlightAsync()
    {
        if (_inFlight.IsEmpty)
        {
            return;
        }

        var running = _inFlight.Keys.ToArray();
        LogDrainingInFlightSteps(_logger, running.Length);
        try
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A step that honoured the stopping token — expected on shutdown.
        }
    }

    [LoggerMessage(EventId = 3130, Level = LogLevel.Error, Message = "Unhandled exception executing step '{StepKey}' (run {RunId}).")]
    private static partial void LogStepFailed(ILogger logger, Exception ex, string stepKey, Guid runId);

    [LoggerMessage(EventId = 3131, Level = LogLevel.Information, Message = "In-memory step runner stopping: waiting for {Count} in-flight step(s) to return.")]
    private static partial void LogDrainingInFlightSteps(ILogger logger, int count);
}
