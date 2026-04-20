# Expressions

Expressions let you reference the trigger payload inside step inputs without writing any C# code. They are declared as string values in `StepMetadata.Inputs` and are resolved at execution time against the persisted trigger data.

## Syntax

An expression starts with `@`. Non-expression strings are passed through as-is.

| Expression | Resolves to |
|---|---|
| `@triggerBody()` | The full trigger body as a `JsonElement` |
| `@triggerBody()?.fieldName` | A top-level field from the trigger body |
| `@triggerBody()?.nested.child` | A nested field (dot-notation path) |
| `@triggerBody()?.items[0].name` | An array element, then a field |
| `@triggerHeaders()` | All trigger headers as a `JsonElement` |
| `@triggerHeaders()['X-Request-Id']` | A specific header value |

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
