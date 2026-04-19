using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using Hangfire;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Built-in step handler for the <c>"ForEach"</c> step type.
/// Resolves the iteration source from an expression or static value, then enqueues
/// each iteration's child steps as individual Hangfire background jobs.
/// </summary>
/// <remarks>
/// Concurrency is controlled by <see cref="LoopStepMetadata.ConcurrencyLimit"/>:
/// items are bucketed and successive buckets receive a small scheduling delay (100ms per bucket)
/// to throttle parallel execution.
/// Child steps receive <c>__loopItem</c> and <c>__loopIndex</c> injected into their inputs.
/// </remarks>
public sealed class ForEachStepHandler : IStepHandler
{
    private readonly IBackgroundJobClient _backgroundJobClient;

    public ForEachStepHandler(IBackgroundJobClient backgroundJobClient)
    {
        _backgroundJobClient = backgroundJobClient;
    }

    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
    {
        if (flow.Manifest.Steps.FindStep(step.Key) is not LoopStepMetadata loopMetadata)
        {
            return ValueTask.FromResult<object?>(null);
        }

        var source = ResolveForEachSource(loopMetadata.ForEach, context.TriggerData, context.TriggerHeaders);
        var items = ToItemList(source);
        if (items.Count == 0)
        {
            return ValueTask.FromResult<object?>(new { iterations = 0 });
        }

        var entryChildren = loopMetadata.Steps
            .Where(kvp => kvp.Value.RunAfter.Count == 0)
            .ToList();

        if (entryChildren.Count == 0 && loopMetadata.Steps.Count > 0)
        {
            entryChildren.Add(loopMetadata.Steps.First());
        }

        var concurrency = Math.Max(1, loopMetadata.ConcurrencyLimit);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var bucket = index / concurrency;
            var startDelay = bucket <= 0
                ? (TimeSpan?)null
                : TimeSpan.FromMilliseconds(bucket * 100.0);

            foreach (var (childKey, childMetadata) in entryChildren)
            {
                var runtimeChildKey = $"{step.Key}.{index}.{childKey}";
                var childInstance = new StepInstance(runtimeChildKey, childMetadata.Type)
                {
                    RunId = context.RunId,
                    PrincipalId = context.PrincipalId,
                    TriggerData = context.TriggerData,
                    TriggerHeaders = context.TriggerHeaders,
                    ScheduledTime = DateTimeOffset.UtcNow + (startDelay ?? TimeSpan.Zero),
                    Index = index,
                    Inputs = BuildChildInputs(childMetadata.Inputs, item, index)
                };

                if (startDelay.HasValue)
                {
                    _backgroundJobClient.Schedule<IHangfireStepRunner>(
                        runner => runner.RunStepAsync(context, flow, childInstance, null),
                        startDelay.Value);
                }
                else
                {
                    _backgroundJobClient.Enqueue<IHangfireStepRunner>(
                        runner => runner.RunStepAsync(context, flow, childInstance, null));
                }
            }
        }

        return ValueTask.FromResult<object?>(new { iterations = items.Count });
    }

    private static IDictionary<string, object?> BuildChildInputs(IDictionary<string, object?> metadataInputs, object? item, int index)
    {
        var result = new Dictionary<string, object?>(metadataInputs, StringComparer.Ordinal);
        result["__loopItem"] = item;
        result["__loopIndex"] = index;
        return result;
    }

    private static object? ResolveForEachSource(object? value, object? triggerData, IReadOnlyDictionary<string, string>? triggerHeaders)
    {
        if (value is not string expression)
        {
            return value;
        }

        object? resolvedBody;
        object? resolvedHeaders;
        var resolvedFromBody = TryResolveTriggerBodyExpression(expression, triggerData, out resolvedBody);
        var resolvedFromHeaders = TryResolveTriggerHeadersExpression(expression, triggerHeaders, out resolvedHeaders);

        if (!resolvedFromBody && !resolvedFromHeaders)
        {
            return value;
        }

        return resolvedBody ?? resolvedHeaders;
    }

    private static List<object?> ToItemList(object? source)
    {
        if (source is null)
        {
            return [];
        }

        if (source is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return element.EnumerateArray()
                .Select(x => (object?)x.Clone())
                .ToList();
        }

        if (source is IEnumerable<object?> objectSequence)
        {
            return objectSequence.ToList();
        }

        if (source is System.Collections.IEnumerable sequence and not string)
        {
            var list = new List<object?>();
            foreach (var item in sequence)
            {
                list.Add(item);
            }
            return list;
        }

        return [];
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
            resolved = triggerData;
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

        if (triggerData is null)
        {
            resolved = null;
            return true;
        }

        var payload = triggerData is JsonElement jsonElement
            ? jsonElement
            : JsonSerializer.SerializeToElement(triggerData);

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
            resolved = headers;
            return true;
        }

        string? headerName = null;
        if (remainder.StartsWith("['", StringComparison.Ordinal) && remainder.EndsWith("']", StringComparison.Ordinal))
            headerName = remainder[2..^2];
        else if (remainder.StartsWith("[\"", StringComparison.Ordinal) && remainder.EndsWith("\"]", StringComparison.Ordinal))
            headerName = remainder[2..^2];

        if (headerName is not null)
        {
            resolved = headers is not null && headers.TryGetValue(headerName, out var value) ? value : null;
            return true;
        }

        return false;
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

