# Extending FlowOrchestrator

FlowOrchestrator has two extension points below the flow-authoring API. Implement **storage** to keep runs in
a database that ships without a built-in provider. Implement a **runtime** to run steps on a queue other than
Hangfire, the in-process channel, or Azure Service Bus. Both plug in through `AddFlowOrchestrator(options => …)`.
Neither needs an internal type: the engine, the planner and the built-in stores are `internal` and are reached
only through the interfaces below.

> Custom step types are not an extension point in this sense. Write an `IStepHandler`; see
> [Step Handlers](step-handlers.md).

## Custom storage

[Storage Backends — Custom Backend](storage.md#custom-backend) lists the interfaces, how to register them,
which are required in 2.0 (`IFlowStore`, `IFlowRunStore`, `IFlowRunRuntimeStore`, `IFlowRunControlStore`,
`IOutputsRepository`), the optional ones (`IFlowSignalStore`, `IFlowScheduleStateStore`, `IFlowRetentionStore`,
`IRunSearchIndex`, the webhook replay and rejection stores), and the default-implemented members you should
override for performance.

What the engine relies on, whatever the database:

- **`TryRecordDispatchAsync` is an idempotent insert.** Two callers recording the same `(runId, stepKey)` get
  exactly one `true`. This is what makes "dispatch many" safe.
- **Step leases (`TryAcquireStepLeaseAsync`) and claims (`TryClaimStepAsync`) are exclusive.** At most one
  worker executes a step at a time; a lease that is not renewed expires, so another worker can take a step
  whose worker died. This is what makes "execute once" hold.
- **Status writes are durable before the call returns.** The continuation reads the frontier right after a
  step completes, on any replica.
- **Timestamps come from one clock.** Lease expiry and timeouts compare stored times; use the database
  server's clock, not each host's.
- **Nothing cascades.** Retention deletes every per-run table explicitly
  ([Observability — Retention](observability.md#data-retention)).

The library's own providers pass one shared contract suite (`tests/shared` in the repository). Run the same
scenarios against your store: concurrent dispatch of one step, two workers racing for one claim, a lease that
expires mid-step, a 500-iteration `ForEach`, and a restart with queued work.

## Custom runtime

A runtime moves step work from the engine to a worker and back. It is three pieces.

### 1. `IStepDispatcher` — hand a step to the queue

```csharp
public sealed class MyQueueStepDispatcher : IStepDispatcher
{
    public ValueTask<string?> EnqueueStepAsync(
        IExecutionContext context, IFlowDefinition flow, IStepInstance step, CancellationToken ct = default)
    {
        // Serialise what the worker needs to rebuild them: context.RunId and trigger data,
        // flow.Id, and the step's key, type and inputs
        // and send the message. Return the queue's message id (shown on the dashboard), or null.
    }

    public ValueTask<string?> ScheduleStepAsync(
        IExecutionContext context, IFlowDefinition flow, IStepInstance step, TimeSpan delay, CancellationToken ct = default)
    {
        // Same message, delivered after `delay` (polling re-checks, retries with back-off).
    }

    // true only when queued messages are lost on restart (an in-process queue). Startup recovery then
    // re-dispatches steps that were recorded as dispatched but never ran.
    public bool LosesQueuedStepsOnRestart => false;
}
```

- **At-least-once delivery is enough.** The engine deduplicates: a duplicate message for a step that another
  worker holds, or that already finished, exits without running the handler.
- **Do not swallow a failed send.** Throw. The engine then releases the step's dispatch record, so the step is
  dispatched again (by the next continuation or by recovery) instead of being marked as queued when it is not.
  If your send can hang, give it a timeout: a dispatch that never returns is re-sent by the unsent-dispatch
  sweep after `FlowRunControlOptions.UnsentDispatchTimeout`.
- **Carry the trace context** (`Activity.Current`) in the message if your queue supports headers, so a run is
  one trace across workers.

### 2. A worker — run the step

For each message, create a DI scope, rebuild the `IExecutionContext` and `IStepInstance` (the public
`ExecutionContext` and `StepInstance` types), find the flow in `FlowRegistry`, and call the engine:

```csharp
await using var scope = services.CreateAsyncScope();
var engine = scope.ServiceProvider.GetRequiredService<IFlowOrchestrator>();
if (!scope.ServiceProvider.GetRequiredService<FlowRegistry>().TryGetFlow(message.FlowId, out var flow))
{
    return; // the flow is no longer registered on this host: acknowledge and drop
}
await engine.RunStepAsync(context, flow, step, stoppingToken);
// Acknowledge the message only after RunStepAsync returns.
```

- **One scope per step.** `IExecutionContextAccessor` and handler dependencies are scoped.
- **Set `context.JobId`** to the message id, so the dashboard can link each attempt to the queue.
- **Acknowledge after the call, not before.** If the worker dies mid-step, the message is redelivered (or the
  lease expires) and the step runs again on a live worker. Handlers are written to be re-runnable.
- **Stop cleanly.** On shutdown, stop taking messages and let in-flight `RunStepAsync` calls finish within the
  host's shutdown timeout; anything cut off is recovered through its lease.

### 3. Cron triggers — `IRecurringTriggerDispatcher` and `IRecurringTriggerInspector`

`IRecurringTriggerDispatcher` registers, updates and removes a flow's cron schedule, and fires a trigger on
demand (`EnqueueTriggerAsync`). When a schedule fires, call `IFlowOrchestrator.TriggerByScheduleAsync(flowId,
triggerKey, jobId)`. Make each firing happen **once across replicas**: use the queue's single delivery or a
lock in your store. `IRecurringTriggerInspector.GetJobsAsync()` lists the schedules for the dashboard.
`AddFlowOrchestrator` registers the runtime-agnostic `IRecurringTriggerSync`, which computes each flow's
effective cron (manifest or persisted override) and calls your dispatcher.

### Registering the runtime

Register all three before `AddFlowOrchestrator` validates the configuration. Startup fails with a message
naming whatever is missing:

```csharp
public static class MyQueueRuntimeExtensions
{
    public static FlowOrchestratorBuilder UseMyQueueRuntime(this FlowOrchestratorBuilder builder)
    {
        builder.Services.TryAddSingleton<IStepDispatcher, MyQueueStepDispatcher>();
        builder.Services.TryAddSingleton<MyQueueCronHub>();
        builder.Services.TryAddSingleton<IRecurringTriggerDispatcher>(sp => sp.GetRequiredService<MyQueueCronHub>());
        builder.Services.TryAddSingleton<IRecurringTriggerInspector>(sp => sp.GetRequiredService<MyQueueCronHub>());
        builder.Services.AddHostedService<MyQueueWorker>();
        return builder;
    }
}

builder.Services.AddFlowOrchestrator(options =>
{
    options.UseSqlServer(connectionString);
    options.UseMyQueueRuntime();
    options.AddFlow<OrderFulfillmentFlow>();
});
```

The `FlowOrchestrator.InMemory` (`UseInMemoryRuntime`) and `FlowOrchestrator.ServiceBus`
(`UseAzureServiceBusRuntime`) packages are complete examples of both shapes: an in-process queue that loses
messages on restart, and a durable broker with scheduled delivery.

## Compatibility

The interfaces on this page are public API. Within 2.x they only gain members, and a new member ships with a
default implementation, so a store or runtime written against 2.0 keeps compiling and working. See
[Support and Release Policy](support-policy.md).
