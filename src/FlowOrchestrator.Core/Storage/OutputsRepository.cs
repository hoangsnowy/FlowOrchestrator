using System.Collections.Concurrent;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Core.Storage;

public interface IOutputsRepository
{
    ValueTask SaveStepOutputAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, IStepResult result);
    ValueTask SaveTriggerDataAsync(ITriggerContext ctx, IFlowDefinition flow, ITrigger trigger);
    ValueTask<object?> GetTriggerDataAsync(Guid runId);
    ValueTask SaveStepInputAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step);
    ValueTask<object?> GetStepOutputAsync(Guid runId, string stepKey);
    ValueTask EndScopeAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step);
    ValueTask RecordEventAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, FlowEvent evt);
}

public sealed class InMemoryOutputsRepository : IOutputsRepository
{
    private static readonly JsonSerializerOptions _webOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, JsonElement> _triggerData = new();
    private readonly ConcurrentDictionary<(Guid RunId, string StepKey), JsonElement> _stepOutputs = new();

    // Converts any object to a self-contained JsonElement.
    // JsonElement values are cloned so they are independent of their source JsonDocument
    // (which may be disposed). Default/undefined JsonElement values are treated as null.
    private static JsonElement ToJsonElement(object? value)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Undefined
                ? JsonSerializer.SerializeToElement<object?>(null, _webOptions)
                : element.Clone();
        }

        return JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), _webOptions);
    }

    public ValueTask SaveStepOutputAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, IStepResult result)
    {
        _stepOutputs[(ctx.RunId, step.Key)] = ToJsonElement(result.Result);
        return ValueTask.CompletedTask;
    }

    public ValueTask SaveTriggerDataAsync(ITriggerContext ctx, IFlowDefinition flow, ITrigger trigger)
    {
        _triggerData[ctx.RunId] = ToJsonElement(trigger.Data);
        return ValueTask.CompletedTask;
    }

    public ValueTask<object?> GetTriggerDataAsync(Guid runId)
    {
        if (_triggerData.TryGetValue(runId, out var element))
        {
            return ValueTask.FromResult<object?>(element);
        }

        return ValueTask.FromResult<object?>(null);
    }

    public ValueTask SaveStepInputAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step)
    {
        _stepOutputs[(ctx.RunId, $"{step.Key}:input")] = ToJsonElement(step.Inputs);
        return ValueTask.CompletedTask;
    }

    public ValueTask<object?> GetStepOutputAsync(Guid runId, string stepKey)
    {
        if (_stepOutputs.TryGetValue((runId, stepKey), out var element))
        {
            return ValueTask.FromResult<object?>(element);
        }

        return ValueTask.FromResult<object?>(null);
    }

    public ValueTask EndScopeAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask RecordEventAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, FlowEvent evt)
    {
        return ValueTask.CompletedTask;
    }
}

public sealed class FlowEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Type { get; init; } = default!;
    public string? StepKey { get; init; }
    public string? Message { get; init; }
}

