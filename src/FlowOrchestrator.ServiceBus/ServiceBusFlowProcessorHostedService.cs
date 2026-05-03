using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.ServiceBus;

/// <summary>
/// Background service that creates one <see cref="ServiceBusProcessor"/> per registered flow,
/// each subscribed to <c>flow-{flowId}</c> on the shared step topic. Drains messages and calls
/// <see cref="IServiceBusFlowRunner.RunStepAsync"/> in a fresh DI scope.
/// </summary>
/// <remarks>
/// Topology: <see cref="ServiceBusTopologyManager.EnsureSubscriptionAsync"/> is called for each
/// registered flow at <see cref="StartAsync"/> time when
/// <see cref="ServiceBusRuntimeOptions.AutoCreateTopology"/> is true. Subscriptions filter on
/// the message's <c>FlowId</c> application property so steps for one flow never enter another
/// flow's processor.
/// <para>
/// Hot-add of flows at runtime is not supported in this version — the set of processors is
/// fixed at <see cref="StartAsync"/>.
/// </para>
/// </remarks>
internal sealed class ServiceBusFlowProcessorHostedService : IHostedService, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusRuntimeOptions _options;
    private readonly ServiceBusTopologyManager _topology;
    private readonly IFlowRepository _repository;
    private readonly IFlowStore _flowStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ServiceBusFlowProcessorHostedService> _logger;

    private readonly Dictionary<Guid, ServiceBusProcessor> _processors = new();

    /// <summary>Initialises the service.</summary>
    public ServiceBusFlowProcessorHostedService(
        ServiceBusClient client,
        ServiceBusRuntimeOptions options,
        ServiceBusTopologyManager topology,
        IFlowRepository repository,
        IFlowStore flowStore,
        IServiceScopeFactory scopeFactory,
        ILogger<ServiceBusFlowProcessorHostedService> logger)
    {
        _client = client;
        _options = options;
        _topology = topology;
        _repository = repository;
        _flowStore = flowStore;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.AutoCreateTopology)
        {
            await _topology.EnsureTopicAsync(cancellationToken).ConfigureAwait(false);
            await _topology.EnsureCronQueueAsync(cancellationToken).ConfigureAwait(false);
        }

        var flows = await _repository.GetAllFlowsAsync().ConfigureAwait(false);
        var storeRecords = await _flowStore.GetAllAsync().ConfigureAwait(false);
        var disabledIds = new HashSet<Guid>(
            storeRecords.Where(r => !r.IsEnabled).Select(r => r.Id));

        foreach (var flow in flows)
        {
            // Skip processor creation for disabled flows. Re-enabling at runtime requires
            // an app restart for the SB processor to come up — documented as a hotfix-time
            // limitation; full hot-reload is a follow-up.
            if (disabledIds.Contains(flow.Id))
            {
                _logger.LogInformation(
                    "Skipping Service Bus processor for flow {FlowId}: flow is disabled.",
                    flow.Id);
                continue;
            }

            if (_options.AutoCreateTopology)
            {
                await _topology.EnsureSubscriptionAsync(flow.Id, cancellationToken).ConfigureAwait(false);
            }

            var processor = _client.CreateProcessor(
                _options.StepTopicName,
                _topology.SubscriptionName(flow.Id),
                new ServiceBusProcessorOptions
                {
                    MaxConcurrentCalls = _options.MaxConcurrentCallsPerSubscription,
                    AutoCompleteMessages = false,
                    ReceiveMode = ServiceBusReceiveMode.PeekLock,
                });
            processor.ProcessMessageAsync += OnMessageAsync;
            processor.ProcessErrorAsync += OnErrorAsync;
            await processor.StartProcessingAsync(cancellationToken).ConfigureAwait(false);
            _processors[flow.Id] = processor;
            _logger.LogInformation("Started Service Bus processor for flow {FlowId}.", flow.Id);
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var (_, processor) in _processors)
        {
            try { await processor.StopProcessingAsync(cancellationToken).ConfigureAwait(false); }
            catch { /* swallow on shutdown */ }
        }
    }

    private async Task OnMessageAsync(ProcessMessageEventArgs args)
    {
        StepEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<StepEnvelope>(args.Message.Body.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialise step message {MessageId}; dead-lettering.", args.Message.MessageId);
            await args.DeadLetterMessageAsync(args.Message, "deserialisation-failed", ex.Message, args.CancellationToken).ConfigureAwait(false);
            return;
        }

        if (envelope is null)
        {
            await args.DeadLetterMessageAsync(args.Message, "null-envelope", null, args.CancellationToken).ConfigureAwait(false);
            return;
        }

        // No in-process dedup needed: as of v1.22 the engine's RunStepAsync acquires an
        // atomic claim at execute-time (FlowStepClaims row), so broadcast delivery (Aspire
        // emulator without SQL filters, or any future at-least-once topology) is correctly
        // serialised at the storage layer — only one concurrent caller wins the claim,
        // others exit silently.
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<IServiceBusFlowRunner>();
            await runner.RunStepAsync(envelope, args.Message, args.CancellationToken).ConfigureAwait(false);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Failed to execute step '{StepKey}' (run {RunId}). Abandoning for redelivery.",
                envelope.StepKey, envelope.RunId);
            // Abandon — engine's claim guard prevents double-execution on redelivery.
            await args.AbandonMessageAsync(args.Message, propertiesToModify: null, args.CancellationToken).ConfigureAwait(false);
        }
    }

    private Task OnErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "Service Bus processor error (Source={Source}, Entity={Entity}).",
            args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var (_, processor) in _processors)
        {
            try { await processor.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        }
        _processors.Clear();
    }
}
