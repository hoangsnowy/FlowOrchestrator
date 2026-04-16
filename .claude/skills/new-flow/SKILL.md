Create a new flow in the FlowOrchestrator sample app, including the flow definition, step handlers, DI registration, and unit tests.

## What to gather first

Before writing any code, ask the user (or infer from context) the following:
- **Flow name** — e.g. `InvoiceProcessing` (becomes `InvoiceProcessingFlow`)
- **Trigger type(s)** — `Manual`, `Cron` (needs cron expression), `Webhook` (needs slug; optional secret)
- **Steps** — list of step names and their handler types (e.g. `validate_invoice` → `ValidateInvoice`)
- **Step dependencies** — which steps run after which (`runAfter`)
- **Create step handlers?** — yes/no for each new handler type that doesn't already exist

If any of the above is ambiguous, ask before writing code.

## Implementation steps (execute without stopping)

### 1. Create the flow definition

File: `samples/FlowOrchestrator.SampleApp/Flows/{Name}Flow.cs`

```csharp
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

/// <summary>
/// {Name}Flow — {brief description of business purpose}
/// 
/// Steps:
///   {step_key} → {StepType} — {what it does}
///   ...
/// </summary>
public sealed class {Name}Flow : IFlowDefinition
{
    // Use a fixed GUID so the flow survives app restarts without duplicate DB entries.
    public Guid Id { get; } = new Guid("{new-guid}");
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            // Add triggers based on user's choice:
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual },
            // ["scheduled"] = new TriggerMetadata
            // {
            //     Type = TriggerType.Cron,
            //     Inputs = new Dictionary<string, object?> { ["cronExpression"] = "0 * * * *" }
            // },
            // ["webhook"] = new TriggerMetadata
            // {
            //     Type = TriggerType.Webhook,
            //     Inputs = new Dictionary<string, object?>
            //     {
            //         ["webhookSlug"] = "{slug}",
            //         // ["webhookSecret"] = "change-me"  // uncomment to require X-Webhook-Key header
            //     }
            // },
        },
        Steps = new StepCollection
        {
            ["{step_key}"] = new StepMetadata
            {
                Type = "{StepType}",
                Inputs = new Dictionary<string, object?>
                {
                    // Static values or trigger expressions:
                    // ["field"] = "@triggerBody()?.field",
                    // ["header"] = "@triggerHeaders()['X-Request-Id']",
                }
            },
            // ["{next_step_key}"] = new StepMetadata
            // {
            //     Type = "{StepType}",
            //     RunAfter = new RunAfterCollection { ["{step_key}"] = [StepStatus.Succeeded] },
            //     Inputs = new Dictionary<string, object?> { }
            // },
        }
    };
}
```

Rules:
- Always use a **fixed, hardcoded GUID** (not `Guid.NewGuid()`) so re-runs don't create duplicate DB rows.
- Use `TriggerType.Manual` / `TriggerType.Cron` / `TriggerType.Webhook` enum values (not string literals).
- Use `StepStatus.Succeeded` / `StepStatus.Failed` in `RunAfterCollection` (not strings).

### 2. Create step handler(s) (if new types are needed)

For each new handler type, follow the pattern in `samples/FlowOrchestrator.SampleApp/Steps/`. See the `/new-step` skill for full details on creating a step handler.

### 3. Register the flow in Program.cs

In `samples/FlowOrchestrator.SampleApp/Program.cs`, add inside `AddFlowOrchestrator(options => { ... })`:

```csharp
options.AddFlow<{Name}Flow>();
```

If you created new step handlers, also add (after the `AddFlowOrchestrator` block):

```csharp
builder.Services.AddStepHandler<{HandlerClass}>("{TypeName}");
```

### 4. Write unit tests

File: `tests/FlowOrchestrator.Core.Tests/{Name}FlowTests.cs`

Test:
- Flow ID is a fixed, non-empty GUID
- Flow has the expected triggers (count + type)
- Flow has the expected steps (count + keys)
- `runAfter` dependencies are wired correctly
- For each webhook trigger: `webhookSlug` is set

Use the same xUnit + FluentAssertions + NSubstitute stack as the rest of the test suite.

### 5. Build and test

```bash
dotnet build
dotnet test
```

Fix all errors. Report: files created, build status, test count before vs after.

## Expression reference (quick cheat sheet)

| Expression | Returns |
|---|---|
| `@triggerBody()` | Full trigger payload as `JsonElement` |
| `@triggerBody()?.field` | Field from payload (null-safe) |
| `@triggerBody()?.nested.child` | Nested field with dot notation |
| `@triggerHeaders()` | All captured headers as dictionary |
| `@triggerHeaders()['X-Request-Id']` | Specific header value |

Captured headers exclude: `Authorization`, `Cookie`, `Set-Cookie`, `X-Webhook-Key`, `Connection`, `Content-Length`, etc.
