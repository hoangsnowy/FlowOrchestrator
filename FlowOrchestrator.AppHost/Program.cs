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

builder.Build().Run();
