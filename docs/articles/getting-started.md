# Getting Started

This guide walks you from zero to a running flow in about five minutes.

## Prerequisites

- .NET 8 SDK or later
- A running SQL Server, PostgreSQL, or nothing (in-memory works fine for local development)
- Hangfire packages: `Hangfire.Core` and either `Hangfire.SqlServer`, `Hangfire.PostgreSql`, or `Hangfire.InMemory`

## Install NuGet Packages

Pick the storage backend that matches your environment:

```bash
# Core + Hangfire (required)
dotnet add package FlowOrchestrator.Core
dotnet add package FlowOrchestrator.Hangfire

# Storage backend — choose one
dotnet add package FlowOrchestrator.SqlServer     # SQL Server via Dapper
dotnet add package FlowOrchestrator.PostgreSQL    # PostgreSQL via Npgsql
dotnet add package FlowOrchestrator.InMemory      # In-process (no external DB)

# Optional REST API + SPA dashboard
dotnet add package FlowOrchestrator.Dashboard
```

## Define Your First Flow

A flow is a plain C# class implementing `IFlowDefinition`. The manifest declares triggers and steps; steps are connected by `runAfter` dependencies.

```csharp
using FlowOrchestrator.Core.Abstractions;

public sealed class HelloWorldFlow : IFlowDefinition
{
    // Stable GUID — must never change after first deployment
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000001");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            // Trigger manually from dashboard or REST API
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual },

            // Also fire every minute automatically
            ["scheduled"] = new TriggerMetadata
            {
                Type = TriggerType.Cron,
                Inputs = new Dictionary<string, object?> { ["cronExpression"] = "*/1 * * * *" }
            }
        },
        Steps = new StepCollection
        {
            ["system_check"] = new StepMetadata
            {
                Type = "LogMessage",
                Inputs = new Dictionary<string, object?> { ["message"] = "System health check starting..." }
            },
            ["system_ready"] = new StepMetadata
            {
                Type = "LogMessage",
                // Only runs after system_check succeeds
                RunAfter = new RunAfterCollection { ["system_check"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["message"] = "All systems operational." }
            }
        }
    };
}
```

> [!NOTE]
> The flow `Id` is a stable identifier stored in the database and used to route Hangfire jobs. **Never change it** after a flow has been deployed to an environment with existing run history.

## Write a Step Handler

Step handlers contain the actual business logic. Register them by type name — the name in `StepMetadata.Type` must match exactly.

```csharp
using FlowOrchestrator.Core.Abstractions;

// Input class — properties map to keys in StepMetadata.Inputs
public sealed class LogMessageInput
{
    public object? Message { get; set; }  // object? because expressions resolve to JsonElement
}

public sealed class LogMessageHandler : IStepHandler<LogMessageInput>
{
    private readonly ILogger<LogMessageHandler> _logger;

    public LogMessageHandler(ILogger<LogMessageHandler> logger) => _logger = logger;

    public ValueTask<object?> ExecuteAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        IStepInstance<LogMessageInput> step)
    {
        var msg = step.Inputs.Message?.ToString() ?? step.Key;
        _logger.LogInformation("[Flow {RunId}] {Step}: {Message}", ctx.RunId, step.Key, msg);
        return ValueTask.FromResult<object?>(new { Logged = msg });
    }
}
```

## Register Everything

### SQL Server

```csharp
var connStr = builder.Configuration.GetConnectionString("FlowOrchestrator")!;

builder.Services.AddHangfire(c => c
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connStr));

builder.Services.AddHangfireServer();

builder.Services.AddFlowOrchestrator(options =>
{
    options.UseSqlServer(connStr);
    options.UseHangfire();
    options.AddFlow<HelloWorldFlow>();
});

builder.Services.AddStepHandler<LogMessageHandler>("LogMessage");

// Optional dashboard
builder.Services.AddFlowDashboard(builder.Configuration);
```

### PostgreSQL

```csharp
var pgConnStr = builder.Configuration.GetConnectionString("FlowOrchestratorPg")!;

builder.Services.AddHangfire(c => c
    .UsePostgreSqlStorage(pgConnStr));     // Hangfire.PostgreSql package

builder.Services.AddHangfireServer();

builder.Services.AddFlowOrchestrator(options =>
{
    options.UsePostgreSql(pgConnStr);
    options.UseHangfire();
    options.AddFlow<HelloWorldFlow>();
});
```

### In-Memory (Dev / Testing)

```csharp
builder.Services.AddHangfire(c => c.UseInMemoryStorage());
builder.Services.AddHangfireServer();

builder.Services.AddFlowOrchestrator(options =>
{
    options.UseInMemory();
    options.UseHangfire();
    options.AddFlow<HelloWorldFlow>();
});
```

> [!WARNING]
> `UseInMemory()` must be called **explicitly** — there is no silent fallback. All run data is lost when the process restarts.

## Map the Dashboard

Add these two lines after `var app = builder.Build();`:

```csharp
app.UseHangfireDashboard("/hangfire");  // Hangfire's own dashboard (optional)
app.MapFlowDashboard("/flows");          // FlowOrchestrator dashboard + REST API
```

## Run the App

```bash
dotnet run
```

Navigate to `http://localhost:5000/flows`. You will see `HelloWorldFlow` listed in the Flows tab. Click **Trigger** to fire a manual run, or wait for the cron trigger to fire.

## Trigger via REST API

```http
POST /flows/api/flows/00000000-0000-0000-0000-000000000001/trigger
Content-Type: application/json

{}
```

The response includes the `runId`:

```json
{ "runId": "3fa85f64-5717-4562-b3fc-2c963f66afa6" }
```

Use that ID to poll the run status:

```http
GET /flows/api/runs/3fa85f64-5717-4562-b3fc-2c963f66afa6
```

## Next Steps

- [Core Concepts](core-concepts.md) — understand flows, manifests, steps, and run lifecycle
- [Step Handlers](step-handlers.md) — write more complex handlers with typed outputs
- [Triggers](triggers.md) — cron schedules, webhooks, and idempotency
- [Polling Steps](polling.md) — wait for external systems without blocking threads
