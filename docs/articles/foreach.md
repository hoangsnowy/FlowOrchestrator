# ForEach Loops

The `ForEach` step type fans out over a collection, executing a child step graph for each item. Iterations can run sequentially or in parallel with a configurable concurrency limit.

## LoopStepMetadata

Use `LoopStepMetadata` instead of `StepMetadata` to declare a loop:

```csharp
["process_orders"] = new LoopStepMetadata
{
    Type = "ForEach",           // always "ForEach" — resolved to the built-in handler
    RunAfter = new RunAfterCollection
    {
        ["prepare"] = [StepStatus.Succeeded]
    },

    // Source collection — literal or expression
    ForEach = "@triggerBody()?.orderIds",

    // Maximum iterations running at the same time
    // 1 = sequential, >1 = parallel fan-out
    ConcurrencyLimit = 2,

    // Steps executed once per item
    Steps = new StepCollection
    {
        ["validate_order"] = new StepMetadata
        {
            Type = "ValidateOrder",
            Inputs = new Dictionary<string, object?>
            {
                ["maxValue"] = 10000  // static — same for every iteration
            }
        }
    }
}
```

## Collection Sources

`ForEach` accepts either a static array or an expression:

```csharp
// Static array
ForEach = new[] { "ORD-001", "ORD-002", "ORD-003" }

// Expression resolved from trigger payload at execution time
ForEach = "@triggerBody()?.orderIds"
```

When the expression resolves to `null` or an empty array, the loop completes as `Succeeded` with zero iterations. Downstream steps (those with `RunAfter = ["process_orders"]`) still run.

## ConcurrencyLimit

| Value | Behaviour |
|---|---|
| `1` | Iterations run one at a time (sequential) |
| `N > 1` | Up to N iterations run simultaneously; remaining items wait for a slot |
| `0` (or omit) | Defaults to `1` (sequential) |

With `ConcurrencyLimit = 2` and 4 items:

```
Iteration 0  ──► validate_order  (slot 1)
Iteration 1  ──► validate_order  (slot 2)
Iteration 2  ──► (waits for slot)
Iteration 3  ──► (waits for slot)
```

When iteration 0 completes, iteration 2 starts. When iteration 1 completes, iteration 3 starts.

## Child Step Key Format

Each child step gets a runtime key in the format `{parentKey}.{index}.{childKey}`:

```
process_orders.0.validate_order
process_orders.1.validate_order
process_orders.2.validate_order
```

These keys appear in the dashboard run timeline and can be used with `IOutputsRepository` to read per-iteration outputs:

```csharp
for (int i = 0; i < itemCount; i++)
{
    var output = await outputs.GetStepOutputAsync(runId, $"process_orders.{i}.validate_order");
    // output is JsonElement?
}
```

## How Dispatch Works

`ForEachStepHandler` does not enqueue jobs directly. Instead it returns a `StepResult` that carries a `DispatchHint` with `Spawn` entries — one per iteration. `FlowOrchestratorEngine` receives the hint, validates that the spawned step keys are not already present in the static DAG, and dispatches each one via `IStepDispatcher`. This keeps runtime dispatch logic in the engine and makes `ForEachStepHandler` portable across all runtime adapters (Hangfire, InMemory, or any future adapter).

## Per-Iteration Injected Inputs

`ForEachStepHandler` injects two additional inputs into each child step before executing it:

| Key | Value | Description |
|---|---|---|
| `__loopItem` | The current item from the collection | The item value (`"ORD-001"`, a number, or a JSON object) |
| `__loopIndex` | Zero-based position | `0`, `1`, `2`, ... |

These are merged with the static `Inputs` defined in the manifest. Declare them as properties in your input class:

```csharp
public sealed class ValidateOrderInput
{
    // Static manifest input
    public decimal MaxValue { get; set; }

    // Injected per iteration
    public object? LoopItem { get; set; }
    public int? LoopIndex { get; set; }
}
```

> [!NOTE]
> The injection keys are `__loopItem` (double-underscore prefix). They will not collide with user-defined input keys as long as those don't start with `__`.

## Full Example: OrderBatchFlow

```csharp
public sealed class OrderBatchFlow : IFlowDefinition
{
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000005");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"]  = new TriggerMetadata { Type = TriggerType.Manual },
            ["webhook"] = new TriggerMetadata
            {
                Type = TriggerType.Webhook,
                Inputs = new Dictionary<string, object?>
                {
                    ["webhookSlug"] = "order-batch"
                }
            }
        },
        Steps = new StepCollection
        {
            // Entry step: logs batch ID from trigger
            ["prepare_batch"] = new StepMetadata
            {
                Type = "LogMessage",
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "@triggerBody()?.batchId"
                }
            },

            // ForEach loop over orderIds from trigger payload
            ["process_orders"] = new LoopStepMetadata
            {
                Type = "ForEach",
                RunAfter = new RunAfterCollection { ["prepare_batch"] = [StepStatus.Succeeded] },
                ForEach = "@triggerBody()?.orderIds",
                ConcurrencyLimit = 2,
                Steps = new StepCollection
                {
                    ["validate_order"] = new StepMetadata
                    {
                        Type = "ProcessOrderItem",
                        Inputs = new Dictionary<string, object?>
                        {
                            ["maxOrderValue"] = 10000  // same for every iteration
                        }
                    }
                }
            },

            // Runs after all iterations complete
            ["finalize_batch"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["process_orders"] = [StepStatus.Succeeded]
                },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "Order batch processing complete."
                }
            }
        }
    };
}
```

## Triggering with a Payload

```http
POST /flows/api/webhook/order-batch
Content-Type: application/json
Idempotency-Key: batch-2026-04-20-001

{
  "batchId": "BATCH-001",
  "orderIds": ["ORD-001", "ORD-002", "ORD-003", "ORD-004"]
}
```

The `Idempotency-Key` header prevents the same batch from being processed twice if the webhook is retried by the sender.

## Nested Loops

`LoopStepMetadata.Steps` supports `LoopStepMetadata` entries — loops can be nested. Each level produces keys with an additional `.{index}.{childKey}` segment. Deep nesting (>2 levels) is supported but adds complexity to key-based output queries.
