# Expressions

Expressions let you reference the trigger payload and prior step outputs inside step inputs without writing any C# code. They are declared as string values in `StepMetadata.Inputs` and are resolved at execution time.

## Syntax

An expression starts with `@`. Non-expression strings are passed through as-is.

### Trigger expressions

| Expression | Resolves to |
|---|---|
| `@triggerBody()` | The full trigger body as a `JsonElement` |
| `@triggerBody()?.fieldName` | A top-level field from the trigger body |
| `@triggerBody()?.nested.child` | A nested field (dot-notation path) |
| `@triggerBody()?.items[0].name` | An array element, then a field |
| `@triggerHeaders()` | All trigger headers as a `JsonElement` |
| `@triggerHeaders()['X-Request-Id']` | A specific header value |

### Step output expressions

Reference the output of a prior step directly in a downstream step's inputs:

| Expression | Resolves to |
|---|---|
| `@steps('key').output` | Full output of step `key` as a `JsonElement` |
| `@steps('key').output.field` | Top-level field from the output (null-safe) |
| `@steps('key').output.nested.child` | Nested field via dot notation |
| `@steps('key').output.items[0]` | Array element by zero-based index |
| `@steps('key').output.items[0].name` | Combined array + nested access |
| `@steps('key').status` | Step status as a string (`"Succeeded"`, `"Failed"`, etc.) |
| `@steps('key').error` | Failure reason when the step failed; `null` otherwise |

Both single and double quotes are accepted for the step key: `@steps('key')` and `@steps("key")` are equivalent.

## Null-Safe Access

The `?.` operator means: if any segment in the path is `null` or missing, the whole expression evaluates to `null` rather than throwing. This matches the C# null-conditional operator semantics.

```csharp
["orderId"] = "@triggerBody()?.orderId"
// → null if body is empty or orderId is missing
// → "ORD-123" if body is { "orderId": "ORD-123" }
```

## Nested Paths

Dot notation traverses nested objects:

```csharp
// Trigger body: { "order": { "customer": { "email": "alice@example.com" } } }
["email"] = "@triggerBody()?.order.customer.email"
// → "alice@example.com"
```

## Array Element Access

Bracket notation accesses array items by zero-based index:

```csharp
// Trigger body: { "items": [{ "sku": "SKU-001" }, { "sku": "SKU-002" }] }
["firstSku"] = "@triggerBody()?.items[0].sku"
// → "SKU-001"
```

## Header Access

```csharp
// Trigger headers include: X-Correlation-Id: abc-123
["correlationId"] = "@triggerHeaders()['X-Correlation-Id']"
// → "abc-123"
```

## Literal Values

Any value that does **not** start with `@` is treated as a literal and passed through unchanged:

```csharp
["status"]   = "pending"          // literal string
["maxItems"] = 10                 // literal int (int, not string)
["enabled"]  = true               // literal bool
["message"]  = "@triggerBody()?.note"  // expression
```

## Security: Excluded Headers

The following headers are **never captured** from trigger requests, regardless of what is sent:

- `Authorization`
- `Cookie`
- `Set-Cookie`
- `X-Auth-Token`
- `X-Api-Key`

These exclusions prevent credentials from being persisted in the database and visible in the dashboard.

## Resolution Timing

Expressions are resolved immediately before `IStepHandler.ExecuteAsync` is called, not when the flow manifest is loaded. This guarantees that the full trigger payload is available even for steps that run minutes or hours after the initial trigger (e.g., after a polling delay).

## Practical Example

A webhook trigger delivers:

```json
{
  "batchId": "BATCH-001",
  "orderIds": ["ORD-001", "ORD-002", "ORD-003"],
  "metadata": { "priority": "high" }
}
```

Step manifest using expressions:

```csharp
Steps = new StepCollection
{
    ["log_batch"] = new StepMetadata
    {
        Type = "LogMessage",
        Inputs = new Dictionary<string, object?>
        {
            ["message"]  = "@triggerBody()?.batchId",    // "BATCH-001"
            ["priority"] = "@triggerBody()?.metadata.priority"  // "high"
        }
    },
    ["process_orders"] = new LoopStepMetadata
    {
        Type = "ForEach",
        ForEach = "@triggerBody()?.orderIds",  // iterates over ["ORD-001", "ORD-002", "ORD-003"]
        // ...
    }
}
```

## Receiving Expressions in Handlers

Because expressions resolve to `JsonElement`, input properties that receive expression values should be typed as `object?`:

```csharp
public sealed class LogMessageInput
{
    public object? Message { get; set; }  // may be string or JsonElement
}
```

Then normalise in the handler:

```csharp
var text = step.Inputs.Message switch
{
    string s when !string.IsNullOrWhiteSpace(s) => s,
    System.Text.Json.JsonElement el             => el.GetRawText(),
    _                                           => step.Key
};
```

## Step Output Expressions in Practice

Step output expressions eliminate the need to inject `IOutputsRepository` into downstream handlers. The manifest declares the dependency, and the engine resolves the value before calling `ExecuteAsync`.

**Before** — handler manually fetches the prior step's output:

```csharp
public sealed class SubmitHandler : IStepHandler<SubmitInput>
{
    private readonly IOutputsRepository _outputs;
    private readonly IExecutionContextAccessor _ctx;

    public async ValueTask<object?> ExecuteAsync(...)
    {
        var prev = await _outputs.GetStepOutputAsync(_ctx.Context!.RunId, "fetch_orders");
        var orderId = ((JsonElement)prev!).GetProperty("orderId").GetString();
        // use orderId...
    }
}
```

**After** — manifest wires the data, handler stays clean:

```csharp
// In the flow manifest:
["submit_to_wms"] = new StepMetadata
{
    Type = "SubmitToWms",
    RunAfter = new RunAfterCollection { ["fetch_orders"] = [StepStatus.Succeeded] },
    Inputs = new Dictionary<string, object?>
    {
        ["orderId"] = "@steps('fetch_orders').output.orderId"
    }
}

// In the handler — no repository injection needed:
public sealed class SubmitInput
{
    public object? OrderId { get; set; }
}

public ValueTask<object?> ExecuteAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance<SubmitInput> step)
{
    var orderId = step.Inputs.OrderId switch
    {
        string s    => s,
        JsonElement el => el.GetString(),
        _           => null
    };
    // use orderId...
}
```

### Null-safety behaviour

- Reference to a step that **exists in the manifest but has not yet run** → `null` (the `runAfter` graph guarantees ordering, so this only occurs with authoring mistakes).
- Reference to a step key **not in the manifest** → throws `FlowExpressionException` at resolution time.
- Field path that does not exist on the output object → `null`.

### Caching

Within a single step execution, all `@steps('key').*` expressions referencing the same step key share a single `IOutputsRepository.GetStepOutputAsync` call. Referencing `@steps('fetch').output.a` and `@steps('fetch').output.b` in the same `Inputs` dictionary costs one repository round-trip, not two.
