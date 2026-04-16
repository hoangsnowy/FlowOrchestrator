Create a new step handler in the FlowOrchestrator sample app, including the input class, handler class, DI registration, and unit tests.

## What to gather first

Before writing any code, ask the user (or infer from context):
- **Handler name** — e.g. `SendEmail` (becomes `SendEmailStepHandler`)
- **Type name** — the string used in flow manifests (e.g. `"SendEmail"`)
- **Input fields** — names and types for the `TInput` class
- **Polling?** — does the step need to poll an external system? (yes → extend `PollableStepHandler<TInput>`, no → implement `IStepHandler<TInput>`)
- **Output** — what object does the handler return on success?
- **Dependencies** — any services the handler needs from DI (e.g. `IHttpClientFactory`, `DbConnectionFactory`)

If ambiguous, ask before writing code.

## Implementation steps (execute without stopping)

### 1. Create the input class

File: `samples/FlowOrchestrator.SampleApp/Steps/{Name}StepInput.cs` (or inline in the handler file if small)

**Plain input:**
```csharp
namespace FlowOrchestrator.SampleApp.Steps;

public sealed class {Name}StepInput
{
    public string {Field} { get; set; } = string.Empty;
    // ... other fields
}
```

**Pollable input** (extend `IPollableInput`):
```csharp
using System.Text.Json.Serialization;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

public sealed class {Name}StepInput : IPollableInput
{
    // Your custom fields:
    public string {Field} { get; set; } = string.Empty;

    // Required polling contract fields:
    public bool PollEnabled { get; set; }
    public int PollIntervalSeconds { get; set; } = 10;
    public int PollTimeoutSeconds { get; set; } = 300;
    public int PollMinAttempts { get; set; } = 1;
    public string? PollConditionPath { get; set; }
    public object? PollConditionEquals { get; set; }

    // Internal state — persisted to SQL between poll attempts (do not rename):
    [JsonPropertyName("__pollStartedAtUtc")]
    public string? PollStartedAtUtc { get; set; }

    [JsonPropertyName("__pollAttempt")]
    public int? PollAttempt { get; set; }
}
```

### 2. Create the handler class

File: `samples/FlowOrchestrator.SampleApp/Steps/{Name}StepHandler.cs`

**Plain handler:**
```csharp
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

public sealed class {Name}StepHandler : IStepHandler<{Name}StepInput>
{
    // Inject DI services via constructor if needed

    public async ValueTask<object?> ExecuteAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance<{Name}StepInput> step)
    {
        var input = step.Inputs;
        // ... implement business logic ...
        return new { /* output fields */ };
    }
}
```

**Pollable handler** (for long-running / async external operations):
```csharp
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

public sealed class {Name}StepHandler : PollableStepHandler<{Name}StepInput>
{
    // Inject DI services via constructor if needed

    protected override async ValueTask<(JsonElement Result, bool IsJson)> FetchAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance<{Name}StepInput> step)
    {
        var input = step.Inputs;
        // Call external service and return the raw JSON result.
        // PollableStepHandler evaluates PollConditionPath / PollConditionEquals
        // automatically. Return (result, true) for JSON, (result, false) for plain string.
        var json = JsonSerializer.SerializeToElement(new { /* result */ });
        return (json, true);
    }
}
```

`PollableStepHandler<T>` manages:
- Tracking `PollAttempt` counter and `PollStartedAtUtc` in the persisted inputs
- Returning `StepStatus.Pending` + `DelayNextStep` when condition not yet met
- Returning `StepStatus.Failed` when `PollTimeoutSeconds` exceeded
- Evaluating `PollConditionPath` / `PollConditionEquals` via JSON path

### 3. Register the handler in Program.cs

```csharp
builder.Services.AddStepHandler<{Name}StepHandler>("{TypeName}");
```

Place this after the `AddFlowOrchestrator` block, alongside the other `AddStepHandler` calls.

### 4. Use in a flow manifest

In any `IFlowDefinition`:
```csharp
["{step_key}"] = new StepMetadata
{
    Type = "{TypeName}",
    Inputs = new Dictionary<string, object?>
    {
        ["{field}"] = "value or @triggerBody()?.field",
        // For polling:
        // ["pollEnabled"] = true,
        // ["pollIntervalSeconds"] = 10,
        // ["pollTimeoutSeconds"] = 120,
        // ["pollConditionPath"] = "status",
        // ["pollConditionEquals"] = "completed",
    }
},
```

### 5. Write unit tests

File: `tests/FlowOrchestrator.Core.Tests/{Name}StepHandlerTests.cs`

Test:
- Happy path: valid inputs → expected output returned
- For pollable handlers: pending (condition not met) → `StepStatus.Pending`; condition met → `StepStatus.Succeeded`; timeout → `StepStatus.Failed`
- Edge cases: null/empty inputs, service errors

Use NSubstitute to mock `IExecutionContext`, `IFlowDefinition`, and any injected services.

### 6. Build and test

```bash
dotnet build
dotnet test
```

Fix all errors. Report: files created, build status, test count before vs after.

## Key patterns from the existing codebase

| Handler | File | Notes |
|---|---|---|
| `LogMessageStepHandler` | `Steps/LogMessageStepHandler.cs` | Simplest possible handler |
| `QueryDatabaseStep` | `Steps/QueryDatabaseStep.cs` | Uses `DbConnectionFactory` from DI |
| `CallExternalApiStep` | `Steps/CallExternalApiStep.cs` | Pollable — extends `PollableStepHandler<T>` |
| `SaveResultStep` | `Steps/SaveResultStep.cs` | Reads previous step outputs from `IOutputsRepository` via `IExecutionContextAccessor` |
| `SerializeProbeStep` | `Steps/SerializeProbeStep.cs` | Parses complex JSON payloads |
