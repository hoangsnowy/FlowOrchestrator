using Azure;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.ServiceBus;

/// <summary>
/// Idempotent helper around <see cref="ServiceBusAdministrationClient"/> that creates the
/// topic, cron queue, and per-flow subscriptions used by the Service Bus runtime.
/// </summary>
/// <remarks>
/// Calls are no-ops when the entity already exists. When
/// <see cref="ServiceBusRuntimeOptions.AutoCreateTopology"/> is <see langword="false"/>,
/// the orchestrator skips this manager entirely — the topology is then expected to come
/// from infrastructure-as-code and the connection string need not carry Manage rights.
/// </remarks>
internal class ServiceBusTopologyManager
{
    private readonly ServiceBusAdministrationClient _admin;
    private readonly ServiceBusRuntimeOptions _options;
    private readonly ILogger<ServiceBusTopologyManager> _logger;

    /// <summary>Initialises the manager with the admin client and options.</summary>
    public ServiceBusTopologyManager(
        ServiceBusAdministrationClient admin,
        ServiceBusRuntimeOptions options,
        ILogger<ServiceBusTopologyManager> logger)
    {
        _admin = admin;
        _options = options;
        _logger = logger;
    }

    /// <summary>Creates the step topic if it does not already exist.</summary>
    public virtual async Task EnsureTopicAsync(CancellationToken ct = default)
    {
        try
        {
            await _admin.CreateTopicAsync(new CreateTopicOptions(_options.StepTopicName)
            {
                RequiresDuplicateDetection = true,
                DuplicateDetectionHistoryTimeWindow = _options.DuplicateDetectionWindow,
            }, ct).ConfigureAwait(false);
            _logger.LogInformation("Created Service Bus topic '{Topic}'.", _options.StepTopicName);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Already exists — fine.
        }
    }

    /// <summary>Creates the cron queue if it does not already exist.</summary>
    public virtual async Task EnsureCronQueueAsync(CancellationToken ct = default)
    {
        try
        {
            await _admin.CreateQueueAsync(new CreateQueueOptions(_options.CronQueueName)
            {
                RequiresDuplicateDetection = true,
                DuplicateDetectionHistoryTimeWindow = _options.DuplicateDetectionWindow,
                MaxDeliveryCount = _options.MaxDeliveryCount,
            }, ct).ConfigureAwait(false);
            _logger.LogInformation("Created Service Bus queue '{Queue}'.", _options.CronQueueName);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Already exists — fine.
        }
    }

    /// <summary>Creates a subscription for <paramref name="flowId"/> with a SQL filter on FlowId, if missing.</summary>
    public virtual async Task EnsureSubscriptionAsync(Guid flowId, CancellationToken ct = default)
    {
        var subscriptionName = SubscriptionName(flowId);
        try
        {
            await _admin.CreateSubscriptionAsync(
                new CreateSubscriptionOptions(_options.StepTopicName, subscriptionName)
                {
                    MaxDeliveryCount = _options.MaxDeliveryCount,
                },
                new CreateRuleOptions("flow-filter", new SqlRuleFilter($"FlowId = '{flowId}'")),
                ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Created subscription '{Subscription}' for flow {FlowId}.",
                subscriptionName, flowId);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Already exists — fine.
        }
    }

    /// <summary>Deletes the per-flow subscription. Used by admin tooling; not called automatically.</summary>
    public async Task RemoveSubscriptionAsync(Guid flowId, CancellationToken ct = default)
    {
        try
        {
            await _admin.DeleteSubscriptionAsync(_options.StepTopicName, SubscriptionName(flowId), ct)
                        .ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone — fine.
        }
    }

    /// <summary>The deterministic subscription name for a flow.</summary>
    public string SubscriptionName(Guid flowId) => $"{_options.SubscriptionPrefix}{flowId}";
}
