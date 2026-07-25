# Conditional Execution

Sometimes status-based gating isn't enough. You need to skip a step *based on the value
of a previous step's output* or *based on the trigger payload* — not just because a
predecessor failed. FlowOrchestrator supports this via the `When` clause on a `RunAfter`
entry.

A step whose `When` evaluates to `false` is recorded as **`Skipped`** (not `Failed`),
and the dashboard surfaces the evaluation trace so you can see exactly why.

## Operator Reference

| Operator | Semantics | Example |
|----------|-----------|---------|
| `==` | Equality | `@steps('x').output.status == 'approved'` |
| `!=` | Inequality | `@triggerBody()?.region != 'EU'` |
| `>` `<` `>=` `<=` | Numeric / ordinal compare | `@steps('x').output.amount >= 1000` |
| `&&` | Logical AND (short-circuits) | `@steps('x').output.score > 5 && @triggerBody().region == 'EU'` |
| `\|\|` | Logical OR (short-circuits) | `@steps('x').output.score > 5 \|\| @triggerBody().priority == 'high'` |
| `!` | Negation | `!@steps('x').output.flag` |
| `( )` | Grouping | `(@steps('x').output.score > 5 \|\| @steps('x').output.age > 10) && @triggerBody().region == 'EU'` |

Either side of a comparison may be a literal (number, single- or double-quoted string, `true`, `false`, `null`), a parenthesised sub-expression, or an `@steps('key').output.path` / `@steps('key').status` / `@steps('key').error` / `@triggerBody()` / `@triggerHeaders()['Header-Name']` expression — e.g. `@steps('a').output.total == @steps('b').output.expected` is valid.

Bare identifiers are **not** allowed: any word other than `true`, `false` and `null` raises a `FlowExpressionException`, and the step is recorded as `Skipped` with the parse error in the trace.

## Type Coercion

Comparison rules are intentionally strict — the evaluator does **not** silently coerce
between types:

* number ↔ number → compared as `decimal`
* string ↔ string → ordinal compare
* bool ↔ bool → equality only
* `null` is equal/not-equal to `null` only; comparing `null` with any other operator throws.
* Mismatched types (e.g. comparing a string to a number) throw `FlowExpressionException`
  and the step is recorded as `Skipped` with the error message in the trace.

## Worked Examples

### 1 — Status-only (legacy syntax — still supported)

```csharp
RunAfter = new RunAfterCollection
{
    ["validate"] = [StepStatus.Succeeded]
}
```

### 2 — When-only

Step runs only if the trigger payload includes a `priority` field equal to `'high'`:

```csharp
RunAfter = new RunAfterCollection
{
    [""] = new RunAfterCondition
    {
        When = "@triggerBody()?.priority == 'high'"
    }
}
```

The empty key `""` is a synthetic entry-trigger marker — useful when you want to
attach a `When` clause to an entry step that has no real predecessor.

### 3 — Combined status + when

```csharp
RunAfter = new RunAfterCollection
{
    ["fetch_order"] = new RunAfterCondition
    {
        Statuses = [StepStatus.Succeeded],
        When     = "@steps('fetch_order').output.amount > 1000"
    }
}
```

Step runs only when `fetch_order` Succeeded **AND** the resolved amount is greater than 1000.

### 4 — Branching (the canonical pattern)

```csharp
["high_value_approve"] = new StepMetadata
{
    Type = "LogMessage",
    RunAfter = new RunAfterCollection
    {
        ["start"] = new RunAfterCondition
        {
            Statuses = [StepStatus.Succeeded],
            When     = "@triggerBody().amount > 1000"
        }
    },
    ...
},

["auto_approve"] = new StepMetadata
{
    Type = "LogMessage",
    RunAfter = new RunAfterCollection
    {
        ["start"] = new RunAfterCondition
        {
            Statuses = [StepStatus.Succeeded],
            When     = "@triggerBody().amount <= 1000"
        }
    },
    ...
},

["complete"] = new StepMetadata
{
    Type = "LogMessage",
    RunAfter = new RunAfterCollection
    {
        ["high_value_approve"] = [StepStatus.Succeeded, StepStatus.Skipped],
        ["auto_approve"]       = [StepStatus.Succeeded, StepStatus.Skipped]
    },
    ...
}
```

`complete` accepts both `Succeeded` and `Skipped` from each branch, so the run always
finishes regardless of which branch took effect. See
[`AmountThresholdFlow`](https://github.com/hoangsnowy/FlowOrchestrator-/blob/main/samples/FlowOrchestrator.SampleApp/Flows/AmountThresholdFlow.cs)
for the runnable sample.

### 5 — Complex boolean

```csharp
["escalate"] = new StepMetadata
{
    RunAfter = new RunAfterCollection
    {
        ["risk_check"] = new RunAfterCondition
        {
            Statuses = [StepStatus.Succeeded],
            When     = "(@steps('risk_check').output.score >= 0.8 || @triggerBody()?.priority == 'high') && @triggerBody().region != 'EU'"
        }
    }
}
```

## Why was my step skipped?

When a step is skipped because its `When` clause evaluated to `false`, the dashboard
shows a **"Why skipped"** panel under the step in the run timeline. The panel displays:

* The original expression text from the manifest
* A rewrite with each LHS replaced by its resolved runtime value
  (e.g. `500 > 1000`)
* The boolean result (always `false`, since that's why it was skipped)

This makes "why didn't this step run?" answerable from the dashboard alone — no log
diving required.

For skips that are **not** driven by a `When` clause — a prerequisite-cascade skip,
or a handler that returns `StepStatus.Skipped` with a `FailedReason` — the step detail
shows the reason text in a **"Skip reason"** panel instead of the trace panel.

## Anti-pattern: throwing exceptions to skip a step

Some authors fake conditional execution by throwing an exception inside a step handler,
hoping the failure cascade will cause downstream steps to be skipped. Don't do this:

* The run history shows a `Failed` status, polluting metrics and alerting.
* The exception message is opaque — readers can't tell whether it was an intentional
  branch or an actual bug.
* Retry logic will retry the "fake failure" repeatedly.

A `When` clause cleanly expresses intent (this branch is *intentionally* not running),
records a clean `Skipped` status, and surfaces the decision in the dashboard.
