using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Dashboard;
using FlowOrchestrator.Hangfire;
using FlowOrchestrator.InMemory;
using FlowOrchestrator.PostgreSQL;
using FlowOrchestrator.SqlServer;
using FlowOrchestrator.SampleApp;
using FlowOrchestrator.SampleApp.Flows;
using FlowOrchestrator.SampleApp.Steps;
using Hangfire;
using Hangfire.SqlServer;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

// Short-circuit `--export-mermaid <flowId|flowName>` before standing up the host.
if (MermaidExportCli.TryHandle(args, out var exitCode))
{
    return exitCode;
}

var builder = WebApplication.CreateBuilder(args);

// ── Storage backend selection ─────────────────────────────────────────────────
// Set via FLOW_STORAGE environment variable (injected by Aspire):
//   "sqlserver"  — SQL Server for both Hangfire and FlowOrchestrator storage
//   "postgresql" — PostgreSQL for FlowOrchestrator; Hangfire.InMemory
//   "inmemory"   — fully in-process, no external dependencies (default)
var storageBackend = builder.Configuration["FLOW_STORAGE"] ?? "inmemory";

// ── Runtime selection ─────────────────────────────────────────────────────────
// Set via RUNTIME environment variable:
//   "hangfire" — IBackgroundJobClient drives step dispatch; cron via RecurringJobManager (default).
//   "inmemory" — Channel<T> drives step dispatch in-process; cron via PeriodicTimer.
// Storage and runtime are independent: any RUNTIME × FLOW_STORAGE combination is valid.
var runtime = (builder.Configuration["RUNTIME"] ?? "hangfire").ToLowerInvariant();
var useHangfireRuntime = runtime == "hangfire";

var sqlConnStr = builder.Configuration.GetConnectionString("FlowOrchestrator");
var pgConnStr  = builder.Configuration.GetConnectionString("FlowOrchestratorPg");

// ── Hangfire (only when runtime=hangfire) ────────────────────────────────────
if (useHangfireRuntime)
{
    builder.Services.AddHangfire(config =>
    {
        config.SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
              .UseSimpleAssemblyNameTypeSerializer()
              .UseRecommendedSerializerSettings();

        if (storageBackend == "sqlserver" && sqlConnStr is not null)
            config.UseSqlServerStorage(sqlConnStr, new SqlServerStorageOptions());
        else
            config.UseInMemoryStorage();
    });

    builder.Services.AddHangfireServer();
}

// ── FlowOrchestrator ──────────────────────────────────────────────────────────
// Sample flows covering the full feature matrix:
//
//   HelloWorldFlow          — minimal cron-scheduled health-check (all backends)
//   ShipmentTrackingFlow    — poll a carrier tracking API (all backends)
//   PaymentEventFlow        — receive payment gateway webhooks (all backends)
//   OrderFulfillmentFlow    — fetch pending orders → call WMS API with polling
//                             (SQL Server only — requires the Orders business table)
//
// vNext additions:
//   OrderBatchFlow          — M1.4 ForEach loop, M1.1 parallel fan-out within ForEach,
//                             M2.3 idempotency via "Idempotency-Key" header (all backends)
//   ParallelHealthCheckFlow — M1.1 DAG multiple entry steps, M1.2 all-of join with
//                             partial failure tolerance, M1.3 graph validation (all backends)
builder.Services.AddFlowOrchestrator(options =>
{
    if (storageBackend == "sqlserver" && sqlConnStr is not null)
        options.UseSqlServer(sqlConnStr);
    else if (storageBackend == "postgresql" && pgConnStr is not null)
        options.UsePostgreSql(pgConnStr);
    else
        options.UseInMemory();

    if (useHangfireRuntime)
        options.UseHangfire();
    else
        options.UseInMemoryRuntime();

    // ── M1.5: Persistent schedule overrides ───────────────────────────────────
    // When true, cron overrides written via the dashboard or API survive process
    // restarts. FlowSyncHostedService loads them from IFlowScheduleStateStore on
    // startup instead of reverting to the manifest cron expression.
    options.Scheduler.PersistOverrides = true;

    // ── M2.2: Default run timeout ─────────────────────────────────────────────
    // Runs that have not completed within this window are marked as TimedOut.
    // Set per-flow via IFlowRunControlStore.SetTimeoutAsync for finer control.
    // Null = no timeout enforced at the framework level.
    options.RunControl.DefaultRunTimeout = TimeSpan.FromMinutes(10);

    // ── M2.3: Idempotency header ──────────────────────────────────────────────
    // When set, webhook and manual triggers extract this header value and pass it
    // to IFlowRunControlStore.TryRegisterIdempotencyKeyAsync. A second request
    // with the same key returns the existing RunId without creating a new run.
    // OrderBatchFlow demonstrates this with "Idempotency-Key: batch-2026-04-19-001".
    options.RunControl.IdempotencyHeaderName = "Idempotency-Key";

    // ── M2.6: Run-data retention ──────────────────────────────────────────────
    // FlowRetentionHostedService sweeps IFlowRetentionStore on the configured
    // interval, deleting runs (and their step/output/event records) older than
    // DataTtl. Disabled by default; enable here for production deployments.
    options.Retention.Enabled = true;
    options.Retention.DataTtl = TimeSpan.FromDays(30);

    // ── M2.5: Observability ───────────────────────────────────────────────────
    // EnableEventPersistence — writes FlowEvent records to IOutputsRepository
    //   (flow_events table), making run timelines visible in the dashboard.
    // EnableOpenTelemetry    — activates FlowOrchestratorTelemetry ActivitySource
    //   and Meter; wire up AddOpenTelemetry() + AddFlowOrchestratorInstrumentation()
    //   in production to export spans/metrics to your observability backend.
    options.Observability.EnableEventPersistence = true;
    options.Observability.EnableOpenTelemetry = true;

    // ── Flow registrations ────────────────────────────────────────────────────
    options.AddFlow<HelloWorldFlow>();
    options.AddFlow<ShipmentTrackingFlow>();
    options.AddFlow<PaymentEventFlow>();

    // M1.4 ForEach + M2.3 Idempotency demo — available on all storage backends.
    options.AddFlow<OrderBatchFlow>();

    // M1.1 Parallel fan-out + M1.2 All-of join + M1.3 Validation demo.
    options.AddFlow<ParallelHealthCheckFlow>();

    // Skipped-step visual demo — a payment flow where validate_payment always fails,
    // charge_customer gets Skipped, handle_decline runs, send_receipt always runs.
    options.AddFlow<ConditionalSkipDemoFlow>();

    // Skip variants demo — shows both a middle skip (chain continues) and an end skip (dead-end).
    options.AddFlow<SkipVariantsDemoFlow>();

    // Dead-end skip demo — entry crashes, all downstream Skipped → run-level status = Failed.
    options.AddFlow<DeadEndSkipDemoFlow>();

    // Final-step skip demo — happy path succeeds; the final error-handler leaf is Skipped
    // because it was never needed → run-level status = Succeeded.
    options.AddFlow<FinalStepSkipDemoFlow>();

    if (storageBackend == "sqlserver")
        options.AddFlow<OrderFulfillmentFlow>();
});

// ── Step handlers ─────────────────────────────────────────────────────────────
builder.Services.AddStepHandler<LogMessageStepHandler>("LogMessage");
builder.Services.AddStepHandler<CallExternalApiStep>("CallExternalApi");
builder.Services.AddStepHandler<SerializeProbeStep>("SerializeProbe");

builder.Services.AddStepHandler<SimulatedFailureStep>("SimulatedFailure");

// M1.4 ForEach child handler — processes a single order item per loop iteration.
// Reads __loopItem (order ID) and __loopIndex injected by ForEachStepHandler at runtime.
// Used by OrderBatchFlow → process_orders scope → validate_order step.
builder.Services.AddStepHandler<ProcessOrderItemStep>("ProcessOrderItem");

// QueryDatabaseStep / SaveResultStep / SampleDataMigrator depend on the Orders
// business table which only exists in the SQL Server instance.
if (storageBackend == "sqlserver" && sqlConnStr is not null)
{
    builder.Services.AddStepHandler<QueryDatabaseStep>("QueryDatabase");
    builder.Services.AddStepHandler<SaveResultStep>("SaveResult");
    builder.Services.AddSingleton(new DbConnectionFactory(sqlConnStr));
    builder.Services.AddHostedService(sp =>
        new SampleDataMigrator(sqlConnStr, sp.GetRequiredService<ILogger<SampleDataMigrator>>()));
}

builder.Services.AddHttpClient("ExternalApi", client =>
{
    client.BaseAddress = new Uri("https://jsonplaceholder.typicode.com");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// ── OpenTelemetry ─────────────────────────────────────────────────────────────
// When running under Aspire, OTEL_EXPORTER_OTLP_ENDPOINT is injected automatically
// and spans/metrics appear in the Aspire Dashboard. Standalone, set the env var to
// point at any OTLP-compatible backend (Jaeger, Grafana, etc.).
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("FlowOrchestrator.SampleApp"))
    .WithTracing(t => t
        .AddFlowOrchestratorInstrumentation()
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddFlowOrchestratorInstrumentation()
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());

builder.Services.AddFlowDashboard(builder.Configuration);

var app = builder.Build();

app.UseStaticFiles();
if (useHangfireRuntime)
    app.UseHangfireDashboard("/hangfire");
app.MapFlowDashboard("/flows");

app.MapGet("/", () =>
    $"OrderHub [runtime={runtime}, storage={storageBackend}] is running. Visit /flows for the dashboard.");

app.Run();

return 0;
