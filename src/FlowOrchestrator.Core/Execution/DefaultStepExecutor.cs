using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Default implementation of <see cref="IStepExecutor"/> that resolves the matching
/// <see cref="IStepHandlerMetadata"/> by type name, evaluates <c>@triggerBody()</c> and
/// <c>@triggerHeaders()</c> input expressions against the current run's trigger data,
/// and delegates execution to the registered handler.
/// </summary>
public sealed class DefaultStepExecutor : IStepExecutor
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IEnumerable<IStepHandlerMetadata> _handlerMetadata;
    private readonly IServiceProvider _serviceProvider;
    private readonly IOutputsRepository _outputsRepository;

    /// <summary>
    /// Constructs the executor with the registered step handler metadata, the service provider used
    /// to resolve handler instances, and the outputs repository for persisting resolved step inputs.
    /// </summary>
    public DefaultStepExecutor(
        IEnumerable<IStepHandlerMetadata> handlerMetadata,
        IServiceProvider serviceProvider,
        IOutputsRepository outputsRepository)
    {
        _handlerMetadata = handlerMetadata;
        _serviceProvider = serviceProvider;
        _outputsRepository = outputsRepository;
    }

    /// <summary>
    /// Resolves inputs, saves them to the output store, then invokes the handler registered for
    /// <paramref name="step"/>'s type. Returns <see cref="StepStatus.Skipped"/> if the step metadata
    /// or its handler cannot be found.
    /// </summary>
    /// <param name="context">The execution context for the current run.</param>
    /// <param name="flow">The flow definition that owns this step.</param>
    /// <param name="step">The step instance with pre-resolved inputs.</param>
    public async ValueTask<IStepResult> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
    {
        var metadata = flow.Manifest.Steps.FindStep(step.Key);
        if (metadata is null)
        {
            return new StepResult
            {
                Key = step.Key,
                Status = StepStatus.Skipped,
                FailedReason = "Step metadata not found."
            };
        }

        step.Inputs = ResolveInputs(step.Inputs, context.TriggerData, context.TriggerHeaders);
        await _outputsRepository.SaveStepInputAsync(context, flow, step).ConfigureAwait(false);

        var handler = _handlerMetadata.FirstOrDefault(h => string.Equals(h.Type, metadata.Type, StringComparison.OrdinalIgnoreCase));
        if (handler is null)
        {
            return new StepResult
            {
                Key = step.Key,
                Status = StepStatus.Skipped,
                FailedReason = $"No handler registered for type '{metadata.Type}'."
            };
        }

        return await handler.ExecuteAsync(_serviceProvider, context, flow, step).ConfigureAwait(false);
    }

    private static IDictionary<string, object?> ResolveInputs(IDictionary<string, object?> inputs, object? triggerData, IReadOnlyDictionary<string, string>? triggerHeaders)
    {
        if (inputs.Count == 0)
        {
            return inputs;
        }

        var resolved = new Dictionary<string, object?>(inputs.Count);
        foreach (var (key, value) in inputs)
        {
            resolved[key] = ResolveValue(value, triggerData, triggerHeaders);
        }

        return resolved;
    }

    private static object? ResolveValue(object? value, object? triggerData, IReadOnlyDictionary<string, string>? triggerHeaders)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                if (TryResolveTriggerBodyExpression(s, triggerData, out var resolvedBody))
                    return resolvedBody;
                if (TryResolveTriggerHeadersExpression(s, triggerHeaders, out var resolvedHeader))
                    return resolvedHeader;
                return s;
            case JsonElement element:
                return ResolveJsonElement(element, triggerData, triggerHeaders);
            case IDictionary<string, object?> dict:
                return ResolveInputs(dict, triggerData, triggerHeaders);
            case IEnumerable<object?> sequence:
                return sequence.Select(item => ResolveValue(item, triggerData, triggerHeaders)).ToArray();
            default:
                return value;
        }
    }

    private static object? ResolveJsonElement(JsonElement element, object? triggerData, IReadOnlyDictionary<string, string>? triggerHeaders)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var stringValue = element.GetString();
            if (TryResolveTriggerBodyExpression(stringValue, triggerData, out var resolvedBody))
                return resolvedBody;
            if (TryResolveTriggerHeadersExpression(stringValue, triggerHeaders, out var resolvedHeader))
                return resolvedHeader;
            return CloneOrNull(element);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var objectValue = JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText(), _jsonOptions)
                              ?? new Dictionary<string, object?>();
            return ResolveInputs(objectValue, triggerData, triggerHeaders);
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var arrayValue = JsonSerializer.Deserialize<List<object?>>(element.GetRawText(), _jsonOptions)
                             ?? [];
            return arrayValue.Select(item => ResolveValue(item, triggerData, triggerHeaders)).ToArray();
        }

        return CloneOrNull(element);
    }

    private static JsonElement? CloneOrNull(JsonElement element)
    {
        try
        {
            return element.Clone();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryResolveTriggerBodyExpression(string? expression, object? triggerData, out object? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        const string prefix = "@triggerBody()";
        var trimmed = expression.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = trimmed[prefix.Length..].Trim();
        if (string.IsNullOrEmpty(remainder))
        {
            resolved = triggerData is null ? null : ToJsonElement(triggerData);
            return true;
        }

        if (remainder.StartsWith("?.", StringComparison.Ordinal))
        {
            remainder = remainder[2..];
        }
        else if (remainder.StartsWith(".", StringComparison.Ordinal))
        {
            remainder = remainder[1..];
        }
        else
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(remainder) || triggerData is null)
        {
            resolved = null;
            return true;
        }

        var payload = ToJsonElement(triggerData);
        if (TryResolvePath(payload, remainder, out var target))
        {
            resolved = target;
            return true;
        }

        resolved = null;
        return true;
    }

    private static bool TryResolveTriggerHeadersExpression(string? expression, IReadOnlyDictionary<string, string>? headers, out object? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        const string prefix = "@triggerHeaders()";
        var trimmed = expression.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = trimmed[prefix.Length..].Trim();
        if (string.IsNullOrEmpty(remainder))
        {
            resolved = headers is null ? null : ToJsonElement(headers);
            return true;
        }

        // Support ['Header-Name'] and ["Header-Name"] notation for headers with dashes
        string? headerName = null;
        if (remainder.StartsWith("['", StringComparison.Ordinal) && remainder.EndsWith("']", StringComparison.Ordinal))
            headerName = remainder[2..^2];
        else if (remainder.StartsWith("[\"", StringComparison.Ordinal) && remainder.EndsWith("\"]", StringComparison.Ordinal))
            headerName = remainder[2..^2];

        if (headerName is not null)
        {
            resolved = headers is not null && headers.TryGetValue(headerName, out var val) ? val : null;
            return true;
        }

        return false;
    }

    private static JsonElement ToJsonElement(object value)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Undefined
                ? JsonSerializer.SerializeToElement<object?>(null, _jsonOptions)
                : element.Clone();
        }

        return JsonSerializer.SerializeToElement(value, value.GetType(), _jsonOptions);
    }

    private static bool TryResolvePath(JsonElement payload, string path, out JsonElement target)
    {
        target = payload;
        var normalizedPath = path.Replace("[", ".", StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal);

        foreach (var segment in normalizedPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (target.ValueKind == JsonValueKind.Object && target.TryGetProperty(segment, out var objectValue))
            {
                target = objectValue;
                continue;
            }

            if (target.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index))
            {
                if (index >= 0 && index < target.GetArrayLength())
                {
                    target = target[index];
                    continue;
                }
            }

            return false;
        }

        return true;
    }
}
