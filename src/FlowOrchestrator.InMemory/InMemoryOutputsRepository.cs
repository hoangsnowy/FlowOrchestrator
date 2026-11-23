using System.Collections.Concurrent;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.InMemory;

/// <summary>
/// In-memory implementation of <see cref="IOutputsRepository"/>.
/// Stores trigger data, trigger headers and step inputs/outputs in concurrent dictionaries keyed by RunId.
/// All data is lost on process restart.
/// </summary>
/// <remarks>
/// Also an <see cref="IFlowRetentionStore"/>: the retention sweep drops the trigger data, headers,
/// step inputs/outputs of every run the paired <see cref="InMemoryFlowRunStore"/> no
/// longer holds, mirroring the SQL backends, which delete <c>FlowOutputs</c> rows together with
/// their run. Without it the repository grew for the lifetime of the process.
/// </remarks>
public sealed class InMemoryOutputsRepository : IOutputsRepository, IFlowRetentionStore
{
    private static readonly JsonSerializerOptions _webOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, JsonElement> _triggerData = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyDictionary<string, string>> _triggerHeaders = new();
    private readonly ConcurrentDictionary<(Guid RunId, string StepKey), JsonElement> _stepOutputs = new();
    private readonly InMemoryFlowRunStore? _runStore;

    /// <summary>Creates a standalone repository; the retention sweep is a no-op without a paired run store.</summary>
    public InMemoryOutputsRepository()
    {
    }

    /// <summary>Creates a repository whose retention sweep follows <paramref name="runStore"/>'s run lifetime.</summary>
    /// <param name="runStore">The run store whose purged runs lose their outputs on the next sweep.</param>
    public InMemoryOutputsRepository(InMemoryFlowRunStore runStore)
    {
        _runStore = runStore;
    }

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
            return ValueTask.FromResult<object?>(element);
        return ValueTask.FromResult<object?>(null);
    }

    public ValueTask SaveTriggerHeadersAsync(ITriggerContext ctx, IFlowDefinition flow, ITrigger trigger)
    {
        if (trigger.Headers is not null)
            _triggerHeaders[ctx.RunId] = trigger.Headers;
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyDictionary<string, string>?> GetTriggerHeadersAsync(Guid runId)
    {
        _triggerHeaders.TryGetValue(runId, out var headers);
        return ValueTask.FromResult<IReadOnlyDictionary<string, string>?>(headers);
    }

    public ValueTask SaveStepInputAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step)
    {
        _stepOutputs[(ctx.RunId, $"{step.Key}:input")] = ToJsonElement(step.Inputs);
        return ValueTask.CompletedTask;
    }

    public ValueTask<object?> GetStepOutputAsync(Guid runId, string stepKey)
    {
        if (_stepOutputs.TryGetValue((runId, stepKey), out var element))
            return ValueTask.FromResult<object?>(element);
        return ValueTask.FromResult<object?>(null);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Removes every entry whose run no longer exists in the paired run store, so it follows the run
    /// store's own cutoff rather than applying <paramref name="cutoffUtc"/> a second time — outputs
    /// carry no completion time of their own. Safe against in-flight runs: the engine
    /// starts a run in the run store before it writes any output for it. Order-independent: entries
    /// whose run is purged later in the same sweep go on the next one. O(entries) per sweep, off the
    /// request path.
    /// </remarks>
    public Task CleanupAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        if (_runStore is null)
        {
            return Task.CompletedTask;
        }

        foreach (var runId in _triggerData.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_runStore.ContainsRun(runId))
            {
                _triggerData.TryRemove(runId, out _);
            }
        }

        foreach (var runId in _triggerHeaders.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_runStore.ContainsRun(runId))
            {
                _triggerHeaders.TryRemove(runId, out _);
            }
        }

        foreach (var key in _stepOutputs.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_runStore.ContainsRun(key.RunId))
            {
                _stepOutputs.TryRemove(key, out _);
            }
        }

        return Task.CompletedTask;
    }
}
