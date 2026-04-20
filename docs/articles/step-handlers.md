# Step Handlers

A step handler is a class that performs the work for one step in a flow. Each handler is registered by name, and the manifest references that name in `StepMetadata.Type`.

## The IStepHandler Contract

```csharp
public interface IStepHandler<TInput>
{
    ValueTask<object?> ExecuteAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        IStepInstance<TInput> step);
}
```

The method receives:

| Parameter | Type | Description |
|---|---|---|
| `ctx` | `IExecutionContext` | Run-scoped context: `RunId`, cancellation token, principal |
| `flow` | `IFlowDefinition` | The flow definition being executed |
| `step` | `IStepInstance<TInput>` | The deserialized step inputs and metadata |

Return `object?` — whatever you return is serialized to JSON and stored as the step output.

## Defining an Input Class

Input properties map directly to keys in `StepMetadata.Inputs`. FlowOrchestrator deserializes the resolved inputs (after expression evaluation) into your input class before calling `ExecuteAsync`.

```csharp
public sealed class SendEmailInput
{
    public string? To { get; set; }
    public string? Subject { get; set; }
    public object? Body { get; set; }  // object? for @triggerBody() expressions
}
```

> [!NOTE]
> Properties that receive `@triggerBody()` expressions should be typed as `object?` because the resolved value is a `JsonElement`. Use `ToString()` or a helper to normalise.

## Minimal Handler Example

```csharp
public sealed class LogMessageHandler : IStepHandler<LogMessageInput>
{
    private readonly ILogger<LogMessageHandler> _logger;

    public LogMessageHandler(ILogger<LogMessageHandler> logger) => _logger = logger;

    public ValueTask<object?> ExecuteAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        IStepInstance<LogMessageInput> step)
    {
        var msg = step.Inputs.Message?.ToString() ?? step.Key;

        _logger.LogInformation(
            "[Flow {RunId}] {Step}: {Message}",
            ctx.RunId, step.Key, msg);

        return ValueTask.FromResult<object?>(new { Logged = msg });
    }
}

public sealed class LogMessageInput
{
    public object? Message { get; set; }
}
```

## Registration

Register handlers in `Program.cs` or a startup extension. The string name must match `StepMetadata.Type` exactly.

```csharp
builder.Services.AddStepHandler<LogMessageHandler>("LogMessage");
builder.Services.AddStepHandler<SendEmailHandler>("SendEmail");
builder.Services.AddStepHandler<QueryDatabaseHandler>("QueryDatabase");
```

Handlers are resolved from DI per-job execution, so they can receive constructor-injected services (`ILogger`, `HttpClient`, `DbConnectionFactory`, etc.).

## Returning Typed Output

Return a plain object or a `StepResult<T>` to explicitly control status and downstream availability:

```csharp
// Plain object — status is inferred as Succeeded
return new { OrderId = orderId, Status = "Validated" };

// Explicit StepResult — gives you control over Key, Status, and failure reason
return new StepResult<OrderResult>
{
    Key = step.Key,
    Value = new OrderResult { OrderId = orderId, Approved = true }
};

// Explicit failure
return new StepResult<OrderResult>
{
    Key = step.Key,
    Status = StepStatus.Failed,
    FailedReason = $"Order {orderId} rejected: insufficient inventory"
};
```

## Reading Upstream Outputs

Downstream handlers can read outputs from any previous step in the same run using `IOutputsRepository`:

```csharp
public sealed class SaveResultHandler : IStepHandler<SaveResultInput>
{
    private readonly IOutputsRepository _outputs;

    public SaveResultHandler(IOutputsRepository outputs) => _outputs = outputs;

    public async ValueTask<object?> ExecuteAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        IStepInstance<SaveResultInput> step)
    {
        // Read the output of a previous step by its step key
        var fetchResult = await _outputs.GetStepOutputAsync(
            ctx.RunId, step.Inputs.FetchStepKey);

        if (fetchResult is null)
            throw new InvalidOperationException("Upstream step output not found.");

        // fetchResult is JsonElement — deserialize as needed
        var orders = fetchResult.Value.Deserialize<List<Order>>();

        // ... save logic
        return new { Saved = orders?.Count ?? 0 };
    }
}
```

## Accessing Execution Context from DI

If your handler calls a service that needs the run context (e.g., an audit logger), inject `IExecutionContextAccessor`:

```csharp
public sealed class AuditLogger
{
    private readonly IExecutionContextAccessor _accessor;

    public AuditLogger(IExecutionContextAccessor accessor) => _accessor = accessor;

    public void Log(string action)
    {
        var runId = _accessor.Current?.RunId;
        // ...
    }
}
```

## Cooperative Cancellation

Check `ctx.CancellationToken` before expensive operations. When a user cancels a run from the dashboard, the token is cancelled at the next Hangfire job boundary:

```csharp
public async ValueTask<object?> ExecuteAsync(
    IExecutionContext ctx, IFlowDefinition flow, IStepInstance<MyInput> step)
{
    ctx.CancellationToken.ThrowIfCancellationRequested();

    await DoExpensiveWorkAsync(ctx.CancellationToken);

    return new { Done = true };
}
```

## Polling Handlers

For steps that need to wait for an external system, use `PollableStepHandler<T>` instead of `IStepHandler<T>` directly. See [Polling Steps](polling.md).
