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

Choose exactly one runtime adapter and select it **inside** the `AddFlowOrchestrator()` callback (`options.UseHangfire()`, `options.UseInMemoryRuntime()`, or `options.UseAzureServiceBusRuntime(...)`). Only when using the Hangfire runtime must Hangfire's own services (`AddHangfire` / `AddHangfireServer`) be registered **before** `AddFlowOrchestrator()`:

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

**InMemory runtime** (no Hangfire server required — `Channel<T>` step dispatcher + `PeriodicTimer` cron):

```csharp
builder.Services.AddFlowOrchestrator(options =>
{
    options.UseInMemory();           // in-process storage
    options.UseInMemoryRuntime();    // Channel<T> dispatcher + PeriodicTimer cron
    options.AddFlow<MyFlow>();       // do NOT call UseHangfire() here
});
```

> [!NOTE]
> When using the InMemory runtime you do not call `AddHangfire()` / `AddHangfireServer()` and do not need a Hangfire storage package (`Hangfire.SqlServer`, `Hangfire.InMemory`). The `FlowOrchestrator.Hangfire` package itself is still required — it hosts the `AddFlowOrchestrator` extension method — and pulls in `Hangfire.Core` transitively. Cron parsing is handled by [Cronos](https://github.com/HangfireIO/Cronos).

> [!TIP]
> Storage and runtime are independent. `UseInMemoryRuntime()` works equally with `UseSqlServer()` or `UsePostgreSql()` if you want in-process step execution but durable run state.

**Azure Service Bus runtime** (v1.22+ — cloud-native, multi-replica, multi-region):

```csharp
builder.Services.AddFlowOrchestrator(options =>
{
    options.UseSqlServer(connStr);     // (or UsePostgreSql / UseInMemory)
    options.UseAzureServiceBusRuntime(sb =>
    {
        sb.ConnectionString   = builder.Configuration.GetConnectionString("ServiceBus")!;
        sb.AutoCreateTopology = true;        // creates topic + sub-per-flow at startup
        // sb.StepTopicName    = "flow-steps";          // defaults shown
        // sb.CronQueueName    = "flow-cron-triggers";
        // sb.SubscriptionPrefix = "flow-";
        // sb.MaxConcurrentCallsPerSubscription = 8;
    });
    options.AddFlow<MyFlow>();
});
```

The Service Bus runtime publishes step messages to a shared topic (`flow-steps`) with one
SQL-filtered subscription per registered flow (filter: `FlowId = '{flowId}'`), and runs cron
triggers as self-perpetuating scheduled messages on a dedicated queue (`flow-cron-triggers`).
The engine's *Dispatch many, Execute once* invariant (dispatch ledger + claim guard) makes
the at-least-once delivery model of Service Bus correct.

`AutoCreateTopology = true` (default) creates topic / queue / subscriptions via
`ServiceBusAdministrationClient` at startup — requires Manage rights on the connection
string. Set to `false` when topology is provisioned via IaC (Bicep / Terraform) and the
deploy identity has only Send / Listen rights.

### ServiceBusRuntimeOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `ConnectionString` | `string` | — | Service Bus namespace connection string. Required; `UseAzureServiceBusRuntime` throws when empty. |
| `StepTopicName` | `string` | `"flow-steps"` | Topic that step-dispatch messages are published to; shared by all registered flows. |
| `CronQueueName` | `string` | `"flow-cron-triggers"` | Queue carrying self-perpetuating scheduled cron-trigger messages. |
| `SubscriptionPrefix` | `string` | `"flow-"` | Prefix for per-flow subscription names — the subscription becomes `flow-{flowId}`. |
| `MaxConcurrentCallsPerSubscription` | `int` | `8` | Messages a single per-flow subscription processor handles concurrently. |
| `AutoCreateTopology` | `bool` | `true` | Create topic / queue / subscriptions at startup via `ServiceBusAdministrationClient`. Requires Manage rights. |
| `DuplicateDetectionWindow` | `TimeSpan` | 10 minutes | Duplicate-detection history window applied to the topic and cron queue when `AutoCreateTopology` is enabled. |
| `MaxDeliveryCount` | `int` | `10` | Delivery attempts before a message is dead-lettered. |

For local development, the included Aspire AppHost wires the official Microsoft Service Bus
emulator via `AddAzureServiceBus("servicebus").RunAsEmulator()` — see
[`FlowOrchestrator.AppHost/Program.cs`](https://github.com/hoangsnowy/FlowOrchestrator/blob/main/FlowOrchestrator.AppHost/Program.cs)
for the wiring; run `dotnet run --project ./FlowOrchestrator.AppHost` and the
`flow-servicebus` instance comes up on port 5104. Note: Aspire 13.2's emulator integration
cannot yet declare SQL filters on subscriptions ([dotnet/aspire#11708](https://github.com/dotnet/aspire/issues/11708)),
so dev-mode topic broadcast is mitigated by an in-process dedup map in the SB processor —
production with `AutoCreateTopology = true` uses real filtered subscriptions and has no
broadcast overhead.

---

## FlowSchedulerOptions

```csharp
options.Scheduler.PersistOverrides = true;
```

| Property | Type | Default | Description |
|---|---|---|---|
| `PersistOverrides` | `bool` | `true` | Persist cron overrides written via dashboard or API to `IFlowScheduleStateStore`. When `true`, overrides survive process restarts; when `false`, the ephemeral in-memory store is used and the manifest cron expression is restored on each restart. |

---

## FlowRunControlOptions

```csharp
options.RunControl.DefaultRunTimeout = TimeSpan.FromMinutes(10);
options.RunControl.IdempotencyHeaderName = "Idempotency-Key";
```

| Property | Type | Default | Description |
|---|---|---|---|
| `DefaultRunTimeout` | `TimeSpan?` | `null` | Global timeout for all runs. Runs exceeding this are marked `TimedOut`. Override per run by including `runTimeoutSeconds` in the trigger payload. `null` = no timeout. |
| `TimeoutEnforcementInterval` | `TimeSpan?` | 30 seconds | How often the periodic sweep proactively marks runs past `TimeoutAtUtc` as `TimedOut`, so a run whose step threw and left nothing scheduled does not sit `Running` forever. `null` or a non-positive value disables the sweep. |
| `IdempotencyHeaderName` | `string` | `"Idempotency-Key"` | Header name checked on trigger requests. If present, the value is used as a deduplication key — a second trigger with the same key returns the existing run instead of creating a new one. |

---

## FlowObservabilityOptions

```csharp
options.Observability.EnableEventPersistence = true;
options.Observability.EnableOpenTelemetry = true;
```

| Property | Type | Default | Description |
|---|---|---|---|
| `EnableEventPersistence` | `bool` | `true` | Write `FlowEvent` records to `IOutputsRepository` (requires the storage backend to implement `IFlowEventReader`). Powers the step timeline in the dashboard. Set to `false` to skip event writes. |
| `EnableOpenTelemetry` | `bool` | `true` | Emit `FlowOrchestratorTelemetry` trace spans and metrics from the engine. The `ActivitySource` and `Meter` are always registered by `AddFlowOrchestrator`; this flag controls whether spans/counters are emitted. Use `AddFlowOrchestratorInstrumentation()` on your OTel pipeline to consume them. |

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
    options.Branding.Title = "My App Workflows";
    options.Branding.Subtitle = "Production";
    options.Branding.LogoUrl = "/logo.svg";
    options.BasicAuth.Username = "admin";
    options.BasicAuth.Password = "changeme";
});
```

Or via `appsettings.json`:

```json
{
  "FlowDashboard": {
    "Branding": {
      "Title": "My App Workflows",
      "Subtitle": "Production",
      "LogoUrl": "/logo.svg"
    },
    "BasicAuth": {
      "Username": "admin",
      "Password": "changeme"
    }
  }
}
```

| Property | Type | Default | Description |
|---|---|---|---|
| `Branding.Title` | `string` | `"FlowOrchestrator Dashboard"` | Displayed in the browser tab and navigation bar |
| `Branding.Subtitle` | `string` | `"Dashboard"` | Small label next to the title (environment, version, etc.) |
| `Branding.LogoUrl` | `string?` | — | URL of a custom logo image shown in the navbar |
| `BasicAuth.Username` | `string?` | — | Basic Auth username |
| `BasicAuth.Password` | `string?` | — | Basic Auth password |
| `BasicAuth.Realm` | `string` | `"FlowOrchestrator Dashboard"` | `WWW-Authenticate` realm returned in 401 responses |
| `WebhookSecurity` | `WebhookSecurityOptions` | (Off) | Enterprise webhook hardening pipeline. Configure via `UseWebhookSecurity(b => …)` — see below. |

> [!NOTE]
> Basic Auth is enforced when **both** `BasicAuth.Username` and `BasicAuth.Password` are set. There is no separate `Enabled` flag — leaving either blank disables authentication.

### WebhookSecurityOptions

Opt-in HMAC signature, replay, rate-limit, IP allow/deny, body cap, and DLQ
gates for the dashboard webhook receive endpoint. The default
`EnforcementMode = Off` keeps the endpoint behaving exactly as in v1.24.

```csharp
// Use the (IConfiguration, Action<FlowDashboardOptions>) overload so appsettings-bound
// Branding/BasicAuth are still applied — the delegate-only overload does NOT read
// configuration and therefore silently disables config-driven Basic Auth.
builder.Services.AddFlowDashboard(builder.Configuration, opts => opts.UseWebhookSecurity(sec =>
{
    sec.UseEnforcementMode(WebhookEnforcementMode.Audit) // dry-run first
       .UseMaxBodyBytes(1_048_576)
       .UseReplayProtection(toleranceSeconds: 300)
       .UseRateLimit(permitsPerSecond: 50, burstSize: 100, perIp: true)
       .UseForwardedHeaders(depth: 1)
       .AllowLegacySha1();   // GitHub legacy X-Hub-Signature opt-in
}));

// Storage backends (multi-replica) — replaces in-memory defaults:
builder.Services.AddFlowOrchestrator(options =>
{
    options.UseSqlServer(sqlConn);
    options.AddSqlServerWebhookHardening(sqlConn);
});
```

> [!WARNING]
> `AddFlowDashboard` has four overloads: `()`, `(IConfiguration, sectionName)`, `(Action<FlowDashboardOptions>)`, and `(IConfiguration, Action<FlowDashboardOptions>, sectionName)`. The delegate-only overload does **not** bind configuration, so combining it with `appsettings.json` Basic Auth silently leaves the dashboard unauthenticated. Use the `(IConfiguration, Action<FlowDashboardOptions>)` overload whenever you need both bound settings and code-only configuration such as `UseWebhookSecurity`.

| Property | Type | Default | Description |
|---|---|---|---|
| `EnforcementMode` | `WebhookEnforcementMode` | `Off` | `Off` skips every gate. `Audit` runs gates + writes DLQ + emits metrics but always returns 202. `Enforce` rejects failing requests. |
| `AllowLegacySha1` | `bool` | `false` | Allow HMAC-SHA1 signatures (legacy GitHub `X-Hub-Signature`). Off by default. |
| `MaxBodyBytes` | `long` | `1_048_576` | Hard cap; oversized requests return 413 before JSON parsing. |
| `ReplayToleranceSeconds` | `int` | `0` | Maximum allowed clock skew. `0` disables replay protection. |
| `DefaultTimestampHeader` | `string?` | — | Global timestamp header default (e.g. `"X-Webhook-Timestamp"`). |
| `DefaultNonceHeader` | `string?` | — | Global nonce / delivery-id header default. |
| `RateLimit.PermitsPerSecond` | `double` | `0` | Sustained throughput. `0` disables rate limiting. |
| `RateLimit.BurstSize` | `int?` | — | Optional burst capacity; defaults to `PermitsPerSecond`. |
| `RateLimit.PerIp` | `bool` | `false` | Key the limiter on `flowId\|clientIp` instead of `flowId`. |
| `ForwardedHeaderDepth` | `int` | `0` | Trusted reverse-proxy depth for `X-Forwarded-For`. `0` uses `RemoteIpAddress` directly. |

**Manifest-level overrides** are documented in [Triggers — Enterprise hardening (v1.25)](triggers.md#enterprise-hardening-v125).

For the full per-publisher cookbook (GitHub, Stripe, Slack, Shopify, …)
see the dedicated [Webhook hardening](webhook-hardening.md) article.

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

### InMemory Runtime (Dev / Testing — no Hangfire server required)

```csharp
builder.Services.AddFlowOrchestrator(options =>
{
    options.UseInMemory();
    options.UseInMemoryRuntime();    // Channel<T> dispatcher + PeriodicTimer cron

    options.RunControl.IdempotencyHeaderName = "Idempotency-Key";
    options.Observability.EnableEventPersistence = true;

    options.AddFlow<HelloWorldFlow>();
});

builder.Services.AddStepHandler<LogMessageHandler>("LogMessage");
builder.Services.AddFlowDashboard(builder.Configuration);

var app = builder.Build();
app.MapFlowDashboard("/flows");
```
