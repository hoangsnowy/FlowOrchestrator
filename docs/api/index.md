# API Reference

Full XML-documentation reference for all public types and members in FlowOrchestrator.

## Packages

| Package | Namespace | Description |
|---|---|---|
| `FlowOrchestrator.Core` | `FlowOrchestrator.Core.*` | Abstractions, execution engine, storage contracts |
| `FlowOrchestrator.Hangfire` | `FlowOrchestrator.Hangfire.*` | Hangfire bridge, DI extensions, built-in step handlers |
| `FlowOrchestrator.InMemory` | `FlowOrchestrator.InMemory.*` | In-process storage backend (dev/testing) |
| `FlowOrchestrator.SqlServer` | `FlowOrchestrator.SqlServer.*` | SQL Server persistence (Dapper) |
| `FlowOrchestrator.PostgreSQL` | `FlowOrchestrator.PostgreSQL.*` | PostgreSQL persistence (Npgsql) |
| `FlowOrchestrator.Dashboard` | `FlowOrchestrator.Dashboard.*` | REST API and embedded SPA |

## Key Types

### Defining Flows

| Type | Description |
|---|---|
| [`IFlowDefinition`](FlowOrchestrator.Core.Abstractions.IFlowDefinition.yml) | Contract for a flow definition class |
| [`FlowManifest`](FlowOrchestrator.Core.Abstractions.FlowManifest.yml) | Holds triggers and steps for a flow |
| [`StepMetadata`](FlowOrchestrator.Core.Abstractions.StepMetadata.yml) | Declares a step's type, inputs, and `runAfter` dependencies |
| [`LoopStepMetadata`](FlowOrchestrator.Core.Abstractions.LoopStepMetadata.yml) | ForEach loop step definition |
| [`TriggerMetadata`](FlowOrchestrator.Core.Abstractions.TriggerMetadata.yml) | Declares a trigger (manual, cron, or webhook) |
| [`RunAfterCollection`](FlowOrchestrator.Core.Abstractions.RunAfterCollection.yml) | Maps predecessor step keys to required statuses |
| [`StepStatus`](FlowOrchestrator.Core.Abstractions.StepStatus.yml) | Enum: `Pending`, `Running`, `Succeeded`, `Failed`, `Skipped` |

### Implementing Step Handlers

| Type | Description |
|---|---|
| [`IStepHandler<TInput>`](FlowOrchestrator.Core.Abstractions.IStepHandler-1.yml) | Main interface for custom step logic |
| [`PollableStepHandler<TInput>`](FlowOrchestrator.Core.Abstractions.PollableStepHandler-1.yml) | Base class for steps that poll an external system |
| [`IPollableInput`](FlowOrchestrator.Core.Abstractions.IPollableInput.yml) | Input contract for pollable steps |
| [`IExecutionContext`](FlowOrchestrator.Core.Abstractions.IExecutionContext.yml) | Run-scoped context passed to every handler |
| [`IExecutionContextAccessor`](FlowOrchestrator.Core.Abstractions.IExecutionContextAccessor.yml) | DI-injectable accessor for the current execution context |

### Storage Abstractions

| Type | Description |
|---|---|
| [`IFlowStore`](FlowOrchestrator.Core.Storage.IFlowStore.yml) | Persists flow definitions |
| [`IFlowRunStore`](FlowOrchestrator.Core.Storage.IFlowRunStore.yml) | Persists run state and step records |
| [`IOutputsRepository`](FlowOrchestrator.Core.Storage.IOutputsRepository.yml) | Stores and retrieves per-step outputs keyed by `RunId` |

### DI Registration

| Type | Description |
|---|---|
| [`FlowOrchestratorBuilder`](FlowOrchestrator.Core.Configuration.FlowOrchestratorBuilder.yml) | Returned by `AddFlowOrchestrator()` — configure storage, Hangfire, and flows |

## See Also

- [Getting Started](../articles/getting-started.md) — install and first flow
- [Core Concepts](../articles/core-concepts.md) — DAG semantics, statuses, RunId
- [Step Handlers](../articles/step-handlers.md) — writing `IStepHandler<T>` and `PollableStepHandler<T>`
- [Storage Backends](../articles/storage.md) — swap SQL Server, PostgreSQL, or InMemory
