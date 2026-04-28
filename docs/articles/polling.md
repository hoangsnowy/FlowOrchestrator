# Polling Steps

The polling pattern lets a step wait for an external system to reach a desired state without holding a thread. Instead of sleeping in a loop, the step returns `StepStatus.Pending` and the runtime reschedules the job after a configurable delay via `IStepDispatcher.ScheduleStepAsync`.

## The Problem

Blocking a thread while waiting for an external API is expensive and ties up worker threads. With polling, each check-in is a short-lived execution that re-schedules itself via the runtime adapter:

```
Attempt 1 → response: { "status": "processing" } → Pending → wait 10s
Attempt 2 → response: { "status": "processing" } → Pending → wait 10s
Attempt 3 → response: { "status": "accepted"   } → Succeeded → continue
```

## PollableStepHandler\<TInput\>

Extend this abstract base class instead of `IStepHandler<T>` directly:

```csharp
public sealed class CheckJobStatusHandler
    : PollableStepHandler<CheckJobStatusInput>
{
    private readonly HttpClient _http;

    public CheckJobStatusHandler(IHttpClientFactory factory)
        => _http = factory.CreateClient("ExternalApi");

    protected override async ValueTask<(JsonElement Result, bool IsJson)> FetchAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        IStepInstance<CheckJobStatusInput> step)
    {
        var response = await _http.GetAsync($"/jobs/{step.Inputs.JobId}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return (doc.RootElement.Clone(), true);
    }
}
```

The base class manages:
- Poll start time tracking across reschedules
- Attempt counting
- Condition evaluation against the response
- Timeout enforcement
- Returning `StepStatus.Pending` with `DelayNextStep` — the engine calls `ReleaseDispatchAsync` then `IStepDispatcher.ScheduleStepAsync(delay)` to reschedule via the active runtime adapter

## Input Class

Your input class must implement `IPollableInput`:

```csharp
public sealed class CheckJobStatusInput : IPollableInput
{
    // Your custom fields
    public string? JobId { get; set; }

    // --- Required by IPollableInput ---
    [JsonPropertyName("pollEnabled")]
    public bool PollEnabled { get; set; } = true;

    [JsonPropertyName("pollIntervalSeconds")]
    public int PollIntervalSeconds { get; set; } = 10;

    [JsonPropertyName("pollTimeoutSeconds")]
    public int PollTimeoutSeconds { get; set; } = 120;

    [JsonPropertyName("pollMinAttempts")]
    public int PollMinAttempts { get; set; } = 1;

    [JsonPropertyName("pollConditionPath")]
    public string? PollConditionPath { get; set; }

    [JsonPropertyName("pollConditionEquals")]
    public string? PollConditionEquals { get; set; }

    // Internal state — managed by PollableStepHandler, persisted between attempts
    [JsonPropertyName("pollStartedAtUtc")]
    public string? PollStartedAtUtc { get; set; }

    [JsonPropertyName("pollAttempt")]
    public int? PollAttempt { get; set; }
}
```

> [!NOTE]
> The `[JsonPropertyName]` attributes are required because step inputs are serialized and deserialized by `System.Text.Json` between execution attempts (across rescheduled jobs or channel messages). Without them the state properties will not survive the round-trip.

## Polling Configuration (Manifest Inputs)

Configure polling behaviour in `StepMetadata.Inputs`:

| Input Key | Type | Default | Description |
|---|---|---|---|
| `pollEnabled` | `bool` | `true` | When `false`, `FetchAsync` runs once and the result is returned directly |
| `pollIntervalSeconds` | `int` | `10` | Seconds between poll attempts |
| `pollTimeoutSeconds` | `int` | `120` | Total time before the step fails as `TimedOut` |
| `pollMinAttempts` | `int` | `1` | Minimum number of attempts before a positive condition is accepted |
| `pollConditionPath` | `string?` | — | Dot-notation JSON path evaluated against the response |
| `pollConditionEquals` | `string?` | — | Expected string value. If omitted, any non-null/non-false value at the path succeeds. |

```csharp
["check_job"] = new StepMetadata
{
    Type = "CheckJobStatus",
    Inputs = new Dictionary<string, object?>
    {
        ["jobId"]               = "@triggerBody()?.jobId",
        ["pollEnabled"]         = true,
        ["pollIntervalSeconds"] = 15,
        ["pollTimeoutSeconds"]  = 300,
        ["pollMinAttempts"]     = 2,
        ["pollConditionPath"]   = "status",
        ["pollConditionEquals"] = "accepted"
    }
}
```

## Condition Evaluation

Given a response `{ "id": 1, "status": "accepted" }`:

- `pollConditionPath = "status"`, `pollConditionEquals = "accepted"` → **matches** when `status == "accepted"`
- `pollConditionPath = "id"` (no `pollConditionEquals`) → **matches** when `id` is truthy (non-null, non-zero, non-false)
- No `pollConditionPath` → condition is considered always met; the step completes after `pollMinAttempts`

Nested paths work: `pollConditionPath = "result.state"` evaluates `response.result.state`.

## Timeout Behaviour

If the elapsed time since the first poll attempt exceeds `pollTimeoutSeconds`, the step fails with:

```
StepStatus.Failed
FailedReason: "Polling timed out after 120 seconds."
```

From the dashboard you can retry the step, which resets the poll clock and starts fresh.

## Disabling Polling

Set `pollEnabled = false` to make a one-shot call:

```csharp
["fetch_config"] = new StepMetadata
{
    Type = "CallExternalApi",
    Inputs = new Dictionary<string, object?>
    {
        ["path"]        = "/config",
        ["pollEnabled"] = false   // single HTTP call, no retry loop
    }
}
```

## Example: OrderFulfillment WMS Integration

```csharp
// From OrderFulfillmentFlow:
["submit_to_wms"] = new StepMetadata
{
    Type = "CallExternalApi",
    RunAfter = new RunAfterCollection { ["fetch_orders"] = [StepStatus.Succeeded] },
    Inputs = new Dictionary<string, object?>
    {
        ["method"]              = "GET",
        ["path"]                = "/posts/1",
        ["pollEnabled"]         = true,
        ["pollIntervalSeconds"] = 10,
        ["pollTimeoutSeconds"]  = 120,
        ["pollConditionPath"]   = "id"   // succeeds when response.id is truthy
    }
}
```

`CallExternalApiStep` extends `PollableStepHandler<CallExternalApiInput>` and calls the configured HTTP endpoint on each attempt. When `response.id` is non-null/non-zero, polling completes and the next step runs.
