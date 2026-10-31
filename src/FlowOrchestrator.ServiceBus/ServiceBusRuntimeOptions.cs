namespace FlowOrchestrator.ServiceBus;

/// <summary>
/// Configuration for the Azure Service Bus runtime adapter.
/// </summary>
public sealed class ServiceBusRuntimeOptions
{
    /// <summary>
    /// Connection string for the Service Bus namespace. Required.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Name of the topic that step dispatch messages are sent to. Default <c>"flow-steps"</c>.
    /// All registered flows share this topic; each flow gets its own subscription with a SQL filter on <c>FlowId</c>.
    /// </summary>
    public string StepTopicName { get; set; } = "flow-steps";

    /// <summary>
    /// Name of the queue used for self-perpetuating cron trigger messages. Default <c>"flow-cron-triggers"</c>.
    /// </summary>
    public string CronQueueName { get; set; } = "flow-cron-triggers";

    /// <summary>
    /// Prefix for per-flow subscription names. Default <c>"flow-"</c>; subscription becomes <c>flow-{flowId}</c>.
    /// </summary>
    public string SubscriptionPrefix { get; set; } = "flow-";

    /// <summary>
    /// Maximum number of messages a single per-flow subscription processor handles concurrently. Default 8.
    /// </summary>
    public int MaxConcurrentCallsPerSubscription { get; set; } = 8;

    /// <summary>
    /// When <see langword="true"/> (default), the adapter creates the topic, queue, and per-flow
    /// subscriptions at startup using <c>ServiceBusAdministrationClient</c>. Set to <see langword="false"/>
    /// in production where the topology is provisioned by IaC and the connection string lacks Manage rights.
    /// </summary>
    public bool AutoCreateTopology { get; set; } = true;

    /// <summary>
    /// Duplicate-detection history window applied to the topic and cron queue when
    /// <see cref="AutoCreateTopology"/> is enabled. Default 10 minutes — long enough to dedup
    /// crash-replay of step messages but short enough not to block legitimate retries.
    /// </summary>
    public TimeSpan DuplicateDetectionWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Maximum delivery attempts before a message is dead-lettered. Default 10.
    /// </summary>
    public int MaxDeliveryCount { get; set; } = 10;

    /// <summary>
    /// When <see langword="true"/> (default), step dispatch sends over its own <c>ServiceBusClient</c> — a
    /// dedicated AMQP connection — instead of the connection shared with every per-flow processor and the
    /// cron processor. Set to <see langword="false"/> to keep a single connection per process.
    /// </summary>
    /// <remarks>
    /// With one shared connection, sends queue behind the receive links of every flow's processor. Measured
    /// on the Service Bus emulator (#192): 104 triggers with 32 in flight went from a 10.1 s send p50 on the
    /// shared connection to 63–83 ms on a dedicated one. The cost is one extra connection per process.
    /// </remarks>
    public bool UseDedicatedSenderConnection { get; set; } = true;
}
