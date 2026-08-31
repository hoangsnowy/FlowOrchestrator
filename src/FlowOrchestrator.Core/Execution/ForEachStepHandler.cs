using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Built-in step handler for the <c>"ForEach"</c> step type.
/// Resolves the iteration source via <see cref="ForEachSourceResolver"/>, then returns a
/// <see cref="StepDispatchHint"/> instructing the engine to enqueue each child step.
/// </summary>
/// <remarks>
/// <para>
/// Concurrency is controlled by <see cref="LoopStepMetadata.ConcurrencyLimit"/>:
/// items are bucketed and successive buckets receive a small scheduling delay (100 ms per bucket)
/// to throttle parallel execution.
/// Child steps receive <c>__loopItem</c> and <c>__loopIndex</c> injected into their inputs.
/// </para>
/// <para>
/// A loop that fans out returns <see cref="StepStatus.Running"/>, not
/// <see cref="StepStatus.Succeeded"/>: the loop step is settled — by <see cref="LoopBarrier"/>,
/// from the continuation of its last child — only once every iteration reached a terminal
/// status. That is what makes a step declaring <c>RunAfter = { loop: [Succeeded] }</c> run
/// <b>after</b> the loop body instead of racing it. A loop with nothing to run (zero items, or
/// an empty body) still returns <see cref="StepStatus.Succeeded"/> immediately — there is no
/// barrier to wait on.
/// </para>
/// </remarks>
public sealed class ForEachStepHandler : IStepHandler
{
    /// <inheritdoc/>
    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
    {
        if (flow.Manifest.Steps.FindStep(step.Key) is not LoopStepMetadata loopMetadata)
        {
            return ValueTask.FromResult<object?>(null);
        }

        var source = ForEachSourceResolver.Resolve(loopMetadata.ForEach, context.TriggerData, context.TriggerHeaders);
        var items = ForEachSourceResolver.ToItemList(source);

        // Nothing to wait for: no items, or a loop declared without a body. Settle immediately —
        // arming the barrier here would park the loop step on children that never exist.
        if (items.Count == 0 || loopMetadata.Steps.Count == 0)
        {
            return ValueTask.FromResult<object?>(new StepResult
            {
                Key = step.Key,
                Status = StepStatus.Succeeded,
                Result = new { iterations = 0 }
            });
        }

        var entryChildren = loopMetadata.Steps
            .Where(kvp => kvp.Value.RunAfter.Count == 0)
            .ToList();

        if (entryChildren.Count == 0 && loopMetadata.Steps.Count > 0)
        {
            entryChildren.Add(loopMetadata.Steps.First());
        }

        var concurrency = Math.Max(1, loopMetadata.ConcurrencyLimit);
        var children = new List<StepDispatchRequest>();

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
                children.Add(new StepDispatchRequest(
                    StepKey: runtimeChildKey,
                    StepType: childMetadata.Type,
                    Inputs: BuildChildInputs(childMetadata.Inputs, item, index),
                    Delay: startDelay));
            }
        }

        // Running arms the completion barrier (see the remarks on this type). The iteration count
        // is persisted with this result and is what LoopBarrier reads back to know how many
        // iterations it must account for before settling the loop step.
        var result = new StepResult
        {
            Key = step.Key,
            Status = StepStatus.Running,
            Result = new { iterations = items.Count },
            DispatchHint = new StepDispatchHint(children)
        };

        return ValueTask.FromResult<object?>(result);
    }

    private static IDictionary<string, object?> BuildChildInputs(IDictionary<string, object?> metadataInputs, object? item, int index)
    {
        var result = new Dictionary<string, object?>(metadataInputs, StringComparer.Ordinal);
        result["__loopItem"] = item;
        result["__loopIndex"] = index;
        return result;
    }
}
