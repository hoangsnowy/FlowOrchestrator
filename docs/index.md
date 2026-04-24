---
_layout: landing
---

# FlowOrchestrator

**Code-first workflow orchestration for .NET — powered by Hangfire.**

Define multi-step background workflows as plain C# classes. Connect them with `runAfter` dependencies. Run them on SQL Server, PostgreSQL, or in-memory. Monitor everything from a built-in dashboard.

<a href="articles/getting-started.md" class="btn btn-primary mr-1">Get Started</a>
<a href="api/index.md" class="btn btn-default">API Reference</a>

---

## Why FlowOrchestrator?

<div class="feature-grid">

### No YAML. No JSON files.
Flows are C# classes — refactorable, IDE-navigable, and version-controlled alongside the code they orchestrate.

### Hangfire under the hood
Durable job execution you already trust. Automatic retries, distributed processing, and persistence — all from Hangfire's proven infrastructure.

### Three trigger types
Manual (dashboard/API), Cron (recurring schedule), and Webhook (external HTTP POST) — declared in the same manifest, no extra config.

### DAG execution planner
`runAfter` dependencies power fan-out, fan-in, and conditional branching. Multiple entry steps run in parallel automatically.

### Built-in polling
`PollableStepHandler<T>` waits for external systems without holding a thread. Hangfire reschedules after each interval, with configurable timeout and min-attempt guards.

### One dashboard, full history
Step-by-step timeline, input/output capture, retry button for failed steps, cooperative cancellation, and schedule management — all at `/flows`.

</div>

---

## Install

```bash
dotnet add package FlowOrchestrator.Core
dotnet add package FlowOrchestrator.Hangfire
dotnet add package FlowOrchestrator.SqlServer      # or .PostgreSQL / .InMemory
dotnet add package FlowOrchestrator.Dashboard      # optional REST API + SPA
```

```csharp
// 1. Register Hangfire (required separately before FlowOrchestrator)
builder.Services.AddHangfire(c => c.UseSqlServerStorage(connectionString));
builder.Services.AddHangfireServer();

// 2. Register FlowOrchestrator
builder.Services.AddFlowOrchestrator(options =>
{
    options.UseSqlServer(connectionString);
    options.UseHangfire();
    options.AddFlow<MyFlow>();
});

// 3. Register step handlers
builder.Services.AddStepHandler<MyStepHandler>("MyStepType");

// 4. Map the dashboard (optional)
app.MapFlowDashboard("/flows");
```

---

## Packages

| Package | Purpose | NuGet |
|---|---|---|
| `FlowOrchestrator.Core` | Abstractions and execution engine | [![NuGet](https://img.shields.io/nuget/v/FlowOrchestrator.Core)](https://www.nuget.org/packages/FlowOrchestrator.Core) |
| `FlowOrchestrator.Hangfire` | Hangfire bridge and DI registration | [![NuGet](https://img.shields.io/nuget/v/FlowOrchestrator.Hangfire)](https://www.nuget.org/packages/FlowOrchestrator.Hangfire) |
| `FlowOrchestrator.SqlServer` | SQL Server persistence (Dapper) | [![NuGet](https://img.shields.io/nuget/v/FlowOrchestrator.SqlServer)](https://www.nuget.org/packages/FlowOrchestrator.SqlServer) |
| `FlowOrchestrator.PostgreSQL` | PostgreSQL persistence (Npgsql) | [![NuGet](https://img.shields.io/nuget/v/FlowOrchestrator.PostgreSQL)](https://www.nuget.org/packages/FlowOrchestrator.PostgreSQL) |
| `FlowOrchestrator.InMemory` | In-process storage (dev/testing) | [![NuGet](https://img.shields.io/nuget/v/FlowOrchestrator.InMemory)](https://www.nuget.org/packages/FlowOrchestrator.InMemory) |
| `FlowOrchestrator.Dashboard` | REST API + embedded SPA | [![NuGet](https://img.shields.io/nuget/v/FlowOrchestrator.Dashboard)](https://www.nuget.org/packages/FlowOrchestrator.Dashboard) |

All packages target **net8.0**, **net9.0**, and **net10.0**. MIT license.
