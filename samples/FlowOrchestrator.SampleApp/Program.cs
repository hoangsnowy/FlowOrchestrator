using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Dashboard;
using FlowOrchestrator.Hangfire;
using FlowOrchestrator.PostgreSQL;
using FlowOrchestrator.SampleApp;
using FlowOrchestrator.SampleApp.Flows;
using FlowOrchestrator.SampleApp.Steps;
using Hangfire;
using Hangfire.SqlServer;

var builder = WebApplication.CreateBuilder(args);

// ── Storage backend selection ─────────────────────────────────────────────────
// Set via FLOW_STORAGE environment variable (injected by Aspire):
//   "sqlserver"  — SQL Server for both Hangfire and FlowOrchestrator storage
//   "postgresql" — PostgreSQL for FlowOrchestrator; Hangfire.InMemory
//   "inmemory"   — fully in-process, no external dependencies (default)
var storageBackend = builder.Configuration["FLOW_STORAGE"] ?? "inmemory";

var sqlConnStr = builder.Configuration.GetConnectionString("FlowOrchestrator");
var pgConnStr  = builder.Configuration.GetConnectionString("FlowOrchestratorPg");

// ── Hangfire ──────────────────────────────────────────────────────────────────
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

// ── FlowOrchestrator ──────────────────────────────────────────────────────────
// OrderHub Sample — four flows covering the full e-commerce lifecycle:
//
//   HelloWorldFlow        — minimal cron-scheduled health-check (all backends)
//   ShipmentTrackingFlow  — poll a carrier tracking API (all backends)
//   PaymentEventFlow      — receive payment gateway webhooks (all backends)
//   OrderFulfillmentFlow  — fetch pending orders → call WMS API with polling
//                           (SQL Server only — requires the Orders business table)
builder.Services.AddFlowOrchestrator(options =>
{
    if (storageBackend == "sqlserver" && sqlConnStr is not null)
        options.UseSqlServer(sqlConnStr);
    else if (storageBackend == "postgresql" && pgConnStr is not null)
        options.UsePostgreSql(pgConnStr);
    // else: InMemory via TryAddSingleton fallback inside AddFlowOrchestrator

    options.UseHangfire();
    options.AddFlow<HelloWorldFlow>();
    options.AddFlow<ShipmentTrackingFlow>();
    options.AddFlow<PaymentEventFlow>();

    if (storageBackend == "sqlserver")
        options.AddFlow<OrderFulfillmentFlow>();
});

// ── Step handlers ─────────────────────────────────────────────────────────────
builder.Services.AddStepHandler<LogMessageStepHandler>("LogMessage");
builder.Services.AddStepHandler<CallExternalApiStep>("CallExternalApi");
builder.Services.AddStepHandler<SerializeProbeStep>("SerializeProbe");

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

builder.Services.AddFlowDashboard(builder.Configuration);

var app = builder.Build();

app.UseStaticFiles();
app.UseHangfireDashboard("/hangfire");
app.MapFlowDashboard("/flows");

app.MapGet("/", () =>
    $"OrderHub [{storageBackend.ToUpperInvariant()}] is running. Visit /flows for the dashboard.");

app.Run();
