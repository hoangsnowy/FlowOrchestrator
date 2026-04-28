using System.Threading.Channels;
using FlowOrchestrator.Core.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.InMemory;

/// <summary>
/// Background service that drains the in-process step channel and executes each step
/// by calling <see cref="IFlowOrchestrator.RunStepAsync"/>.
/// </summary>
/// <remarks>
/// This service is the in-memory equivalent of a Hangfire worker thread.
/// Steps dispatched by <see cref="InMemoryStepDispatcher"/> are consumed here one at a time.
/// For parallelism, configure the channel with a bounded capacity and increase
/// the degree of parallelism via the <see cref="MaxConcurrency"/> property (default: 1).
/// </remarks>
internal sealed class InMemoryStepRunnerHostedService : BackgroundService
{
    private readonly ChannelReader<InMemoryStepEnvelope> _reader;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InMemoryStepRunnerHostedService> _logger;

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
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

        await foreach (var envelope in _reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            await semaphore.WaitAsync(stoppingToken).ConfigureAwait(false);

            // Run each step in a separate scope so scoped services (e.g. IExecutionContextAccessor) are isolated.
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var engine = scope.ServiceProvider.GetRequiredService<IFlowOrchestrator>();
                    await engine.RunStepAsync(envelope.Context, envelope.Flow, envelope.Step, stoppingToken)
                                .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex,
                        "Unhandled exception executing step '{StepKey}' (run {RunId}).",
                        envelope.Step.Key, envelope.Context.RunId);
                }
                finally
                {
                    semaphore.Release();
                }
            }, stoppingToken);
        }
    }
}
