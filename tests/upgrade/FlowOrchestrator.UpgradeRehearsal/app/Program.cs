// OrderHub — the upgrade rehearsal's customer host, as written against FlowOrchestrator 1.34.7.
// See ../README.md. Everything below "Rehearsal knobs" exists only so the rehearsal script can
// leave work queued across the upgrade; the rest is ordinary customer wiring.
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.HealthChecks;
using FlowOrchestrator.Dashboard;
using FlowOrchestrator.Dashboard.Webhooks;
using FlowOrchestrator.Hangfire;
#if STORAGE_SQLSERVER
using FlowOrchestrator.SqlServer;
#else
using FlowOrchestrator.PostgreSQL;
#endif
#if RUNTIME_HANGFIRE
using Hangfire;
#if STORAGE_POSTGRESQL
using Hangfire.PostgreSql;
#endif
#else
using FlowOrchestrator.ServiceBus;
#endif
using OrderHub;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("FlowOrchestrator")
    ?? throw new InvalidOperationException("ConnectionStrings:FlowOrchestrator is required.");

// ── Rehearsal knobs ───────────────────────────────────────────────────────────
// Rehearsal:Workers=false runs the host as a "web-only" node: it accepts triggers and webhooks and
// writes the dispatches, but nothing consumes them, so they are still queued when the host stops.
var workers = builder.Configuration.GetValue("Rehearsal:Workers", true);

#if RUNTIME_HANGFIRE
builder.Services.AddHangfire(config =>
{
    config.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
          .UseSimpleAssemblyNameTypeSerializer()
          .UseRecommendedSerializerSettings();
#if STORAGE_SQLSERVER
    config.UseSqlServerStorage(connectionString);
#else
    config.UsePostgreSqlStorage(o => o.UseNpgsqlConnection(connectionString));
#endif
});
if (workers)
{
    builder.Services.AddHangfireServer(o => o.SchedulePollingInterval = TimeSpan.FromSeconds(1));
}
#endif

builder.Services.AddFlowOrchestrator(options =>
{
#if STORAGE_SQLSERVER
    options.UseSqlServer(connectionString);
    options.AddSqlServerWebhookHardening(connectionString);
#else
    options.UsePostgreSql(connectionString);
    options.AddPostgreSqlWebhookHardening(connectionString);
#endif

#if RUNTIME_HANGFIRE
    options.UseHangfire();
#else
    options.UseAzureServiceBusRuntime(sb =>
    {
        sb.ConnectionString = builder.Configuration.GetConnectionString("ServiceBus")
            ?? throw new InvalidOperationException("ConnectionStrings:ServiceBus is required.");
        // The emulator has no management plane: entities come from its Config.json.
        sb.AutoCreateTopology = false;
    });
#endif

    options.Scheduler.PersistOverrides = true;
    options.RunControl.IdempotencyHeaderName = "Idempotency-Key";
    options.Retention.Enabled = true;
    options.Retention.DataTtl = TimeSpan.FromDays(30);
    options.Observability.EnableEventPersistence = true;

    options.AddFlow<OrderWebhookFlow>();
    options.AddFlow<NightlyReportFlow>();
    options.AddFlow<LegacyCleanupFlow>();
    options.AddFlow<ApprovalFlow>();
    options.AddFlow<ShipmentTrackingFlow>();
    options.AddFlow<ShipmentBatchFlow>();
    options.AddFlow<PaymentFlow>();
});

builder.Services.AddStepHandler<LogMessageStep>("LogMessage");
builder.Services.AddStepHandler<CarrierStatusStep>("CarrierStatus");
builder.Services.AddStepHandler<ChargeCardStep>("ChargeCard");

builder.Services.AddFlowDashboard(builder.Configuration, dashboard => dashboard.UseWebhookSecurity(sec =>
    sec.UseEnforcementMode(WebhookEnforcementMode.Enforce)
       .UseReplayProtection(toleranceSeconds: 3600)));

builder.Services.AddHealthChecks().AddFlowOrchestratorHealthChecks();

#if RUNTIME_SERVICEBUS
if (!workers)
{
    // Web-only node for Service Bus: drop the consumers so sent messages stay in the subscriptions.
    RehearsalKnobs.RemoveHostedServices(builder.Services, "ServiceBusFlowProcessorHostedService", "ServiceBusCronProcessorHostedService");
}
#endif

var app = builder.Build();

app.MapFlowDashboard("/flows");
app.MapHealthChecks("/health");
app.MapGet("/", () => "OrderHub is running. Dashboard: /flows");

if (builder.Configuration.GetValue("Rehearsal:ShutdownEndpoint", false))
{
    // Graceful stop for the rehearsal script (the same path as SIGTERM / Ctrl+C), on every OS.
    app.MapPost("/rehearsal/shutdown", (IHostApplicationLifetime lifetime) =>
    {
        lifetime.StopApplication();
        return Results.Accepted();
    });
}

app.Run();
