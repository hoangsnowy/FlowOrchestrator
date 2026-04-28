# Configuration Reference

Complete reference for all options available on `FlowOrchestratorBuilder` and `FlowDashboardOptions`.

## FlowOrchestratorBuilder

### Storage

| Method | Description |
|---|---|
| `options.UseSqlServer(string connStr)` | Configure SQL Server persistence (Dapper) |
| `options.UsePostgreSql(string connStr)` | Configure PostgreSQL persistence (Npgsql) |
| `options.UseInMemory()` | Configure in-process storage (no persistence) |

Exactly one of these **must** be called. Omitting all three throws `InvalidOperationException` on startup.

### Flows

```csharp
options.AddFlow<TFlow>()
```

Registers a flow class. `TFlow` must implement `IFlowDefinition` and have a public parameterless constructor. Call once per flow.

### Runtime Adapter

Choose exactly one runtime adapter and register it **before** calling `AddFlowOrchestrator()`:

**Hangfire runtime** (SQL Server or PostgreSQL persistence, distributed workers):

```csharp
builder.Services.AddHangfire(...);
builder.Services.AddHangfireServer();

builder.Services.AddFlowOrchestrator(options =>
{
    options.UseSqlServer(connStr);  // or UsePostgreSql(...)
    options.UseHangfire();           // wires HangfireStepDispatcher
    options.AddFlow<MyFlow>();
});
```

**InMemory runtime** (no Hangfire packages required — Channel&lt;T&gt;-backed, no external dependencies):

```csharp
// Call AddInMemoryRuntime() BEFORE AddFlowOrchestrator()
builder.Services.AddInMemoryRuntime();

builder.Services.AddFlowOrchestrator(options =>
{
    options.UseInMemory();           // in-process storage
    options.AddFlow<MyFlow>();       // do NOT call UseHangfire() here
});
```

> [!NOTE]
> When using the InMemory runtime, Hangfire packages (`Hangfire.Core`, `Hangfire.InMemory`, etc.) are not needed and should not be added.

---

## FlowSchedulerOptions

```csharp
options.Scheduler.PersistOverrides = true;
```

| Property | Type | Default | Description |
|---|---|---|---|
| `PersistOverrides` | `bool` | `false` | Persist cron overrides written via dashboard or API to `IFlowScheduleStateStore`. When `true`, overrides survive process restarts; when `false`, the manifest cron expression is restored on each restart. |

---

## FlowRunControlOptions

```csharp
options.RunControl.DefaultRunTimeout = TimeSpan.FromMinutes(10);
options.RunControl.IdempotencyHeaderName = "Idempotency-Key";
```

| Property | Type | Default | Description |
|---|---|---|---|
| `DefaultRunTimeout` | `TimeSpan?` | `null` | Global timeout for all runs. Runs exceeding this are marked `TimedOut`. Set per-flow via `IFlowRunControlStore.SetTimeoutAsync` for finer control. `null` = no timeout. |
| `IdempotencyHeaderName` | `string` | `"Idempotency-Key"` | Header name checked on trigger requests. If present, the value is used as a deduplication key — a second trigger with the same key returns the existing run instead of creating a new one. |

---

## FlowObservabilityOptions

```csharp
options.Observability.EnableEventPersistence = true;
options.Observability.EnableOpenTelemetry = true;
```

| Property | Type | Default | Description |
|---|---|---|---|
| `EnableEventPersistence` | `bool` | `false` | Write `FlowEvent` records to `IOutputsRepository` (requires the storage backend to implement `IFlowEventReader`). Powers the step timeline in the dashboard. |
| `EnableOpenTelemetry` | `bool` | `false` | Register `FlowOrchestratorTelemetry` `ActivitySource` and `Meter`. Use `AddFlowOrchestratorInstrumentation()` on your OTel pipeline to consume them. |

---

## FlowRetentionOptions

```csharp
options.Retention.Enabled = true;
options.Retention.DataTtl = TimeSpan.FromDays(30);
options.Retention.SweepInterval = TimeSpan.FromHours(1);
```

| Property | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `bool` | `false` | Start `FlowRetentionHostedService`. Disabled by default; opt in explicitly for production. |
| `DataTtl` | `TimeSpan` | 30 days | Age threshold — runs older than this are deleted on each sweep. |
| `SweepInterval` | `TimeSpan` | 1 hour | Interval between sweep passes. |

---

## FlowDashboardOptions

```csharp
builder.Services.AddFlowDashboard(options =>
{
    options.Title = "My App Workflows";
    options.Subtitle = "Production";
    options.LogoUrl = "/logo.svg";
    options.BasicAuth.Enabled = true;
    options.BasicAuth.Username = "admin";
    options.BasicAuth.Password = "changeme";
});
```

Or via `appsettings.json`:

```json
{
  "FlowDashboard": {
    "Title": "My App Workflows",
    "Subtitle": "Production",
    "LogoUrl": "/logo.svg",
    "BasicAuth": {
      "Enabled": true,
      "Username": "admin",
      "Password": "changeme"
    }
  }
}
```

| Property | Type | Default | Description |
|---|---|---|---|
| `Title` | `string` | `"FlowOrchestrator"` | Displayed in the browser tab and navigation bar |
| `Subtitle` | `string?` | — | Small label next to the title (environment, version, etc.) |
| `LogoUrl` | `string?` | — | URL of a custom logo image shown in the navbar |
| `BasicAuth.Enabled` | `bool` | `false` | Protect all dashboard routes with HTTP Basic Auth |
| `BasicAuth.Username` | `string?` | — | Required when `BasicAuth.Enabled = true` |
| `BasicAuth.Password` | `string?` | — | Required when `BasicAuth.Enabled = true` |

---

## Complete Examples

### Hangfire Runtime (SQL Server)

```csharp
builder.Services.AddHangfire(c => c
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString));

builder.Services.AddHangfireServer();

builder.Services.AddFlowOrchestrator(options =>
{
    options.UseSqlServer(connectionString);
    options.UseHangfire();

    // Scheduler
    options.Scheduler.PersistOverrides = true;

    // Run control
    options.RunControl.DefaultRunTimeout = TimeSpan.FromMinutes(10);
    options.RunControl.IdempotencyHeaderName = "Idempotency-Key";

    // Observability
    options.Observability.EnableEventPersistence = true;
    options.Observability.EnableOpenTelemetry = true;

    // Retention
    options.Retention.Enabled = true;
    options.Retention.DataTtl = TimeSpan.FromDays(30);
    options.Retention.SweepInterval = TimeSpan.FromHours(1);

    // Flows
    options.AddFlow<HelloWorldFlow>();
    options.AddFlow<OrderFulfillmentFlow>();
    options.AddFlow<OrderBatchFlow>();
});

// Step handlers
builder.Services.AddStepHandler<LogMessageHandler>("LogMessage");
builder.Services.AddStepHandler<QueryDatabaseHandler>("QueryDatabase");

// Dashboard
builder.Services.AddFlowDashboard(builder.Configuration);

var app = builder.Build();
app.UseHangfireDashboard("/hangfire");
app.MapFlowDashboard("/flows");
```

### InMemory Runtime (Dev / Testing — no Hangfire required)

```csharp
// Register InMemory runtime BEFORE AddFlowOrchestrator
builder.Services.AddInMemoryRuntime();

builder.Services.AddFlowOrchestrator(options =>
{
    options.UseInMemory();

    options.RunControl.IdempotencyHeaderName = "Idempotency-Key";
    options.Observability.EnableEventPersistence = true;

    options.AddFlow<HelloWorldFlow>();
});

builder.Services.AddStepHandler<LogMessageHandler>("LogMessage");
builder.Services.AddFlowDashboard(builder.Configuration);

var app = builder.Build();
app.MapFlowDashboard("/flows");
```
