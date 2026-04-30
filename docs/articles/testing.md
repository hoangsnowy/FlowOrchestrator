# Testing flows with `FlowOrchestrator.Testing`

`FlowOrchestrator.Testing` is a lightweight in-process helper that lets you write integration tests for your flows without spinning up ASP.NET, Hangfire, or a real database. A single `FlowTestHost.For<TFlow>()…BuildAsync()` call wires up the in-memory storage and runtime, registers your handlers, and returns a host you can trigger and assert on.

```csharp
await using var host = await FlowTestHost.For<MyFlow>()
    .WithHandler<MyHandler>("MyStep")
    .BuildAsync();

var result = await host.TriggerAsync(body: new { orderId = "ord_123" });

Assert.Equal(RunStatus.Succeeded, result.Status);
Assert.Equal(StepStatus.Succeeded, result.Steps["my_step"].Status);
```

## Install

```bash
dotnet add package FlowOrchestrator.Testing
```

The package depends on `FlowOrchestrator.Core` and `FlowOrchestrator.InMemory`, so you don't need to add those separately. It's framework-agnostic — use it from xUnit, NUnit, MSTest, or anything else.

## Builder API

Every `With*` call returns the same builder for chaining; `BuildAsync()` returns a started host.

| Method | Purpose |
|---|---|
| `WithHandler<THandler>("StepType")` | Registers a step handler the same way `services.AddStepHandler<T>()` does. |
| `WithService<TService>(instance)` | Registers a fake or pre-built singleton (e.g. an `IOrderRepository` test double). |
| `WithService<TService, TImpl>()` | Registers a singleton implementation resolved from DI. |
| `WithLogging(builder => ...)` | Configures the logging pipeline (defaults to silent). |
| `WithSystemClock(now)` | Freezes the clock used by the in-memory cron dispatcher. |
| `WithFastPolling(maxDelay = null)` | Caps polling reschedule delays so a 30s manifest interval collapses to ~100ms in tests. |
| `WithCustomConfiguration(opts => ...)` | Escape hatch for advanced `FlowOrchestratorBuilder` tweaks. |

## Triggering a run

```csharp
// Manual trigger (default)
var result = await host.TriggerAsync(
    triggerKey: "manual",
    body: new { orderId = "ord_123" },
    headers: new Dictionary<string, string> { ["X-Correlation-Id"] = "abc" },
    timeout: TimeSpan.FromSeconds(10));

// Webhook trigger — sugar over TriggerAsync with type = "Webhook"
var result = await host.TriggerWebhookAsync(
    slug: "order-fulfillment",
    body: new { orderId = "ord_123" },
    headers: new Dictionary<string, string> { ["X-Webhook-Key"] = "secret" });
```

The host polls `IFlowRunStore` until the run reaches a terminal status or `timeout` elapses. The default timeout is 30 seconds. `result.TimedOut` is `true` when the timeout fired before completion.

## Result API

```csharp
result.RunId          // Guid the engine assigned
result.Status         // Succeeded | Failed | Cancelled | TimedOut | Running
result.Duration       // Wall-clock time TriggerAsync spent waiting
result.TimedOut       // true when test-host timeout fired (run may still be in flight)
result.Events         // Persisted event log (recorded automatically)
result.Steps["my_step"].Status        // Per-step terminal status
result.Steps["my_step"].Output        // JsonElement of the handler's output
result.Steps["my_step"].Inputs        // JsonElement of the resolved inputs
result.Steps["my_step"].FailureReason // Captured exception text on Failed
result.AttemptCount("my_step")        // Total attempts (including retries)
```

## Patterns

### 1. Happy path

```csharp
[Fact]
public async Task OrderFulfillment_succeeds_for_valid_order()
{
    await using var host = await FlowTestHost.For<OrderFulfillmentFlow>()
        .WithHandler<FetchOrdersHandler>("FetchOrders")
        .WithHandler<SubmitToWmsHandler>("SubmitToWms")
        .WithService<IOrderRepository>(new FakeOrderRepository())
        .BuildAsync();

    var result = await host.TriggerAsync(body: new { orderId = "ord_123" });

    Assert.Equal(RunStatus.Succeeded, result.Status);
    Assert.Equal(StepStatus.Succeeded, result.Steps["submit_to_wms"].Status);
}
```

### 2. Failure path — exception is captured, not thrown

A handler that throws does **not** propagate the exception out of `TriggerAsync`. The engine catches it, records the step as `Failed`, and the run as `Failed`. Assert on the result:

```csharp
[Fact]
public async Task Order_with_invalid_total_marks_run_Failed()
{
    await using var host = await FlowTestHost.For<MyFlow>()
        .WithHandler<ThrowingHandler>("MyStep")
        .BuildAsync();

    var result = await host.TriggerAsync();

    Assert.Equal(RunStatus.Failed, result.Status);
    Assert.Contains("expected reason", result.Steps["my_step"].FailureReason!);
}
```

### 3. Manual retry

The engine does not auto-retry on failure. Trigger, observe the failure, then call `IFlowOrchestrator.RetryStepAsync` to re-run the step:

```csharp
var orchestrator = host.Services.GetRequiredService<IFlowOrchestrator>();
var flow = host.Services.GetServices<IFlowDefinition>().OfType<MyFlow>().Single();

var first = await host.TriggerAsync();
Assert.Equal(RunStatus.Failed, first.Status);

await orchestrator.RetryStepAsync(flow.Id, first.RunId, "flaky_step");
var second = await host.WaitForRunAsync(first.RunId, TimeSpan.FromSeconds(5));

Assert.Equal(2, second.AttemptCount("flaky_step"));
Assert.Equal(RunStatus.Succeeded, second.Status);
```

### 4. Polling — collapse manifest interval to ~100ms

`PollableStepHandler<T>` reads `pollIntervalSeconds` from the manifest input. Without help, a `pollIntervalSeconds = 30` in production becomes a 30-second wait per attempt in tests. `WithFastPolling()` clamps the dispatcher's reschedule delay so attempts run back-to-back:

```csharp
await using var host = await FlowTestHost.For<MyPollingFlow>()
    .WithHandler<MyPollingHandler>("MyPolling")
    .WithFastPolling()                      // 30s manifest → ~100ms in tests
    .BuildAsync();

var result = await host.TriggerAsync();

Assert.Equal(RunStatus.Succeeded, result.Status);
```

You can pass a custom cap: `WithFastPolling(TimeSpan.FromMilliseconds(50))`.

> `WithFastPolling()` also installs a permissive runtime claim store so polling reschedules can re-dispatch the same step. This is a test-only relaxation — production claim guards are unaffected.

### 5. Cron — freeze the clock and fast-forward

```csharp
await using var host = await FlowTestHost.For<HelloWorldFlow>()
    .WithHandler<LogMessageStepHandler>("LogMessage")
    .WithSystemClock(new DateTimeOffset(2026, 4, 30, 10, 0, 0, TimeSpan.Zero))
    .BuildAsync();

await host.FastForwardAsync(TimeSpan.FromMinutes(1));

// Wait for the next 1-second tick of the in-memory cron loop to read the new clock.
var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
while (DateTime.UtcNow < deadline && !someCounter.Fired) await Task.Delay(100);
```

> The in-memory cron dispatcher polls every real second. `FastForwardAsync` advances the frozen clock instantly, but you still need to wait at least one real second for the next tick to read it.

### 6. Substituting external HTTP calls

Wire a fake `HttpMessageHandler` (e.g. via `Microsoft.Extensions.Http.Polly` or [`RichardSzalay.MockHttp`](https://www.nuget.org/packages/RichardSzalay.MockHttp/)):

```csharp
var mockHttp = new MockHttpMessageHandler();
mockHttp.When("https://api.example.com/*").Respond("application/json", "{\"id\":1}");

var factory = mockHttp.ToHttpClientFactory();

await using var host = await FlowTestHost.For<MyFlow>()
    .WithHandler<CallExternalApiStep>("CallExternalApi")
    .WithService<IHttpClientFactory>(factory)
    .BuildAsync();
```

## Anti-pattern: real `pollIntervalSeconds` in tests

Don't rely on production poll intervals (10s+) in tests — they make every test slow and flaky. Always pair pollable flows with `WithFastPolling()`. If you genuinely need to assert behaviour over real time, write a separate slow-test category and tag it accordingly.

## Disposal

`FlowTestHost<TFlow>` is `IAsyncDisposable`. Use `await using` to guarantee the host stops cleanly between tests:

```csharp
await using var host = await FlowTestHost.For<MyFlow>()...BuildAsync();
```

After disposal, calls to `TriggerAsync` throw `ObjectDisposedException`.
