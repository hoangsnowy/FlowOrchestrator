using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// ── SQL Server resource ───────────────────────────────────────────────────────
var sqlPassword = builder.AddParameter("sql-password", secret: true);
var sql = builder.AddSqlServer("sql", password: sqlPassword)
                 .WithDataVolume()
                 .AddDatabase("FlowOrchestrator");

// ── PostgreSQL resource ───────────────────────────────────────────────────────
var postgres = builder.AddPostgres("postgres")
                      .WithDataVolume()
                      .AddDatabase("FlowOrchestratorPg");

// ── Azure Service Bus emulator (via Aspire integration) ──────────────────────
// Aspire.Hosting.Azure.ServiceBus's RunAsEmulator() pulls
// mcr.microsoft.com/azure-messaging/servicebus-emulator + the SQL Edge sidecar
// behind the scenes — no manual networking, no JSON config file. Topics, queues,
// and subscriptions are declared programmatically below; the Aspire integration
// generates the emulator's runtime configuration from these declarations.
//
// Subscription names match the per-flow naming convention used by the
// FlowOrchestrator.ServiceBus adapter (`flow-{flowId}`) so that
// SERVICEBUS_AUTO_CREATE_TOPOLOGY=false works out of the box.
//
// Caveat: Aspire 13.2 does NOT yet expose programmatic SQL filters on
// subscriptions (dotnet/aspire#11708). Without a filter every subscription
// receives every message; the engine's claim guard still ensures each step
// is executed exactly once, but the emulator does extra work per step.
// Production Azure namespaces should run with AutoCreateTopology=true so the
// adapter creates filtered subscriptions via ServiceBusAdministrationClient.
var serviceBus = builder
    .AddAzureServiceBus("servicebus")
    .RunAsEmulator();

var stepTopic = serviceBus.AddServiceBusTopic("flow-steps");
serviceBus.AddServiceBusQueue("flow-cron-triggers");

foreach (var flowId in SampleFlowIds.All)
{
    stepTopic.AddServiceBusSubscription($"flow-{flowId}");
}

// ── Instance 1: SQL Server backend ──────────────────────────────── port 5101 ─
// WithEndpoint overrides the port on the existing endpoint created from launchSettings.json
// (calling WithHttpEndpoint would add a duplicate and throw)
builder.AddProject<Projects.FlowOrchestrator_SampleApp>("flow-sqlserver")
       .WithEnvironment("FLOW_STORAGE", "sqlserver")
       .WithEndpoint("http",  e => e.Port = 5101)
       .WithEndpoint("https", e => e.Port = 7101)
       .WithReference(sql)
       .WaitFor(sql);

// ── Instance 2: PostgreSQL backend ──────────────────────────────── port 5102 ─
builder.AddProject<Projects.FlowOrchestrator_SampleApp>("flow-postgresql")
       .WithEnvironment("FLOW_STORAGE", "postgresql")
       .WithEndpoint("http",  e => e.Port = 5102)
       .WithEndpoint("https", e => e.Port = 7102)
       .WithReference(postgres)
       .WaitFor(postgres);

// ── Instance 3: Fully in-memory — no Hangfire, no database ──────── port 5103 ─
// Demonstrates RUNTIME=inmemory + FLOW_STORAGE=inmemory: Channel<T>-backed step
// dispatcher and PeriodicTimer-backed cron, all in-process. /hangfire returns 404
// because Hangfire is not registered in this mode.
builder.AddProject<Projects.FlowOrchestrator_SampleApp>("flow-inmemory")
       .WithEnvironment("FLOW_STORAGE", "inmemory")
       .WithEnvironment("RUNTIME", "inmemory")
       .WithEndpoint("http",  e => e.Port = 5103)
       .WithEndpoint("https", e => e.Port = 7103);

// ── Instance 4: Service Bus runtime + InMemory storage ────────── port 5104 ─
// Demonstrates RUNTIME=servicebus: step dispatch via Azure Service Bus topic
// (1 subscription per flow), cron via self-perpetuating scheduled messages
// on a queue. Topology auto-creation is OFF; the Aspire-managed emulator
// pre-declares all entities. WithReference("servicebus") injects the
// `ConnectionStrings__servicebus` env var that the SampleApp reads.
builder.AddProject<Projects.FlowOrchestrator_SampleApp>("flow-servicebus")
       .WithEnvironment("FLOW_STORAGE", "inmemory")
       .WithEnvironment("RUNTIME", "servicebus")
       .WithEnvironment("SERVICEBUS_AUTO_CREATE_TOPOLOGY", "false")
       .WithEndpoint("http",  e => e.Port = 5104)
       .WithEndpoint("https", e => e.Port = 7104)
       .WithReference(serviceBus)
       .WaitFor(serviceBus);

builder.Build().Run();

/// <summary>
/// Stable flow identifiers hardcoded in samples/FlowOrchestrator.SampleApp/Flows/*.cs.
/// Listed here so the AppHost can pre-declare matching SB subscriptions without
/// a runtime dependency on the sample-app project.
/// </summary>
internal static class SampleFlowIds
{
    public static readonly Guid[] All =
    [
        new("00000000-0000-0000-0000-000000000001"), // HelloWorldFlow
        new("00000000-0000-0000-0000-000000000002"), // OrderFulfillmentFlow
        new("00000000-0000-0000-0000-000000000003"), // ShipmentTrackingFlow
        new("00000000-0000-0000-0000-000000000004"), // PaymentEventFlow
        new("00000000-0000-0000-0000-000000000005"), // OrderBatchFlow
        new("00000000-0000-0000-0000-000000000006"), // ParallelHealthCheckFlow
        new("00000000-0000-0000-0000-000000000007"), // ApprovalWorkflowFlow
        new("00000000-0000-0000-0000-000000000008"), // ConditionalSkipDemoFlow
        new("00000000-0000-0000-0000-000000000009"), // SkipVariantsDemoFlow
        new("00000000-0000-0000-0000-000000000010"), // DeadEndSkipDemoFlow
        new("00000000-0000-0000-0000-000000000011"), // FinalStepSkipDemoFlow
        new("00000000-0000-0000-0000-000000000012"), // AmountThresholdFlow
    ];
}
