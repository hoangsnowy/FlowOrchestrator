using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Default implementation of <see cref="IStepExecutor"/> that resolves the matching
/// <see cref="IStepHandlerMetadata"/> by type name, evaluates <c>@triggerBody()</c>,
/// <c>@triggerHeaders()</c>, and <c>@steps()</c> input expressions, and delegates
/// execution to the registered handler.
/// </summary>
public sealed class DefaultStepExecutor : IStepExecutor
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IEnumerable<IStepHandlerMetadata> _handlerMetadata;
    private readonly IServiceProvider _serviceProvider;
    private readonly IOutputsRepository _outputsRepository;
    private readonly IFlowRunStore _runStore;

    /// <summary>
    /// Constructs the executor with the registered step handler metadata, service provider,
    /// outputs repository, and run store.
    /// </summary>
    public DefaultStepExecutor(
        IEnumerable<IStepHandlerMetadata> handlerMetadata,
        IServiceProvider serviceProvider,
        IOutputsRepository outputsRepository,
        IFlowRunStore runStore)
    {
        _handlerMetadata = handlerMetadata;
        _serviceProvider = serviceProvider;
        _outputsRepository = outputsRepository;
        _runStore = runStore;
    }

    /// <summary>
    /// Resolves inputs (trigger and step-output expressions), saves them to the output store,
    /// then invokes the handler registered for <paramref name="step"/>'s type.
    /// Returns <see cref="StepStatus.Skipped"/> if the step metadata or its handler cannot be found.
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

        // Pass 1 (sync): resolve @triggerBody() and @triggerHeaders() expressions.
        step.Inputs = ResolveInputs(step.Inputs, context.TriggerData, context.TriggerHeaders);

        // Pass 2 (async): resolve @steps('key').output|status|error expressions.
        var resolver = new StepOutputResolver(_outputsRepository, _runStore, context.RunId, flow.Manifest.Steps);
        step.Inputs = await ResolveStepExpressionsAsync(step.Inputs, resolver).ConfigureAwait(false);

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

    // ── Trigger expression resolution (synchronous) ───────────────────────────

    private static IDictionary<string, object?> ResolveInputs(IDictionary<string, object?> inputs, object? triggerData, IReadOnlyDictionary<string, string>? triggerHeaders)
    {
        if (inputs.Count == 0)
            return inputs;

        var resolved = new Dictionary<string, object?>(inputs.Count);
        foreach (var (key, value) in inputs)
            resolved[key] = ResolveValue(value, triggerData, triggerHeaders);

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
            return null;

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
            var arrayValue = JsonSerializer.Deserialize<List<object?>>(element.GetRawText(), _jsonOptions) ?? [];
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

    // Trigger-body / trigger-headers expression resolution lives in the canonical
    // TriggerExpressionResolver. The thin pass-through methods below preserve the
    // historical local-call call sites in this file without re-implementing the
    // logic. Both gain the StartsWithAt fast-path and the trim-elision wins for
    // free.
    private static bool TryResolveTriggerBodyExpression(string? expression, object? triggerData, out object? resolved)
        => TriggerExpressionResolver.TryResolveTriggerBodyExpression(expression, triggerData, out resolved);

    private static bool TryResolveTriggerHeadersExpression(string? expression, IReadOnlyDictionary<string, string>? headers, out object? resolved)
        => TriggerExpressionResolver.TryResolveTriggerHeadersExpression(expression, headers, out resolved);

    // ── Step-output expression resolution (async) ─────────────────────────────

    private static async ValueTask<IDictionary<string, object?>> ResolveStepExpressionsAsync(
        IDictionary<string, object?> inputs,
        StepOutputResolver resolver)
    {
        if (inputs.Count == 0)
            return inputs;

        var resolved = new Dictionary<string, object?>(inputs.Count);
        foreach (var (key, value) in inputs)
            resolved[key] = await ResolveStepValueAsync(value, resolver).ConfigureAwait(false);

        return resolved;
    }

    private static async ValueTask<object?> ResolveStepValueAsync(object? value, StepOutputResolver resolver)
    {
        switch (value)
        {
            case null:
                return null;

            case string s when StepOutputResolver.IsStepExpression(s):
                return await resolver.ResolveAsync(s).ConfigureAwait(false);

            case JsonElement { ValueKind: JsonValueKind.String } element:
            {
                var str = element.GetString();
                if (StepOutputResolver.IsStepExpression(str))
                    return await resolver.ResolveAsync(str!).ConfigureAwait(false);
                return value;
            }

            case IDictionary<string, object?> dict:
                return await ResolveStepExpressionsAsync(dict, resolver).ConfigureAwait(false);

            case IEnumerable<object?> sequence:
            {
                var items = new List<object?>();
                foreach (var item in sequence)
                    items.Add(await ResolveStepValueAsync(item, resolver).ConfigureAwait(false));
                return items.ToArray();
            }

            default:
                return value;
        }
    }
}
