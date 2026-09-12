using System.Collections.Frozen;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Default implementation of <see cref="IStepExecutor"/> that resolves the matching
/// <see cref="IStepHandlerMetadata"/> by type name, evaluates <c>@triggerBody()</c>,
/// <c>@triggerHeaders()</c>, and <c>@steps()</c> input expressions via the
/// <see cref="InputResolutionPipeline"/> + <see cref="StepExpressionResolutionPipeline"/>,
/// injects the enclosing ForEach iteration context via <see cref="LoopScopeInputs"/>,
/// and delegates execution to the registered handler.
/// </summary>
public sealed class DefaultStepExecutor : IStepExecutor
{
    private readonly FrozenDictionary<string, IStepHandlerMetadata> _handlersByType;
    private readonly IServiceProvider _serviceProvider;
    private readonly IOutputsRepository _outputsRepository;
    private readonly IFlowRunStore _runStore;

    /// <summary>
    /// Constructs the executor with the registered step handler metadata, service provider,
    /// outputs repository, and run store.
    /// </summary>
    /// <param name="handlerMetadata">
    /// Every registered handler. Indexed by step type once here rather than scanned per execution:
    /// the lookup used to be a <c>FirstOrDefault</c> with a closure, i.e. O(registered handlers)
    /// plus an allocation on every step the engine runs. The registry is fixed for the lifetime of
    /// the executor, which is exactly the case <see cref="FrozenDictionary{TKey, TValue}"/> exists
    /// for — build once, read forever, faster reads than <see cref="Dictionary{TKey, TValue}"/>.
    /// </param>
    /// <param name="serviceProvider">Used to resolve the handler instance for the matched type.</param>
    /// <param name="outputsRepository">Persists resolved step inputs and outputs.</param>
    /// <param name="runStore">Run/step bookkeeping.</param>
    /// <remarks>
    /// Duplicate registrations for the same type keep the FIRST entry, matching the previous
    /// <c>FirstOrDefault</c> behaviour. Matching stays ordinal case-insensitive.
    /// </remarks>
    public DefaultStepExecutor(
        IEnumerable<IStepHandlerMetadata> handlerMetadata,
        IServiceProvider serviceProvider,
        IOutputsRepository outputsRepository,
        IFlowRunStore runStore)
    {
        var byType = new Dictionary<string, IStepHandlerMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in handlerMetadata)
        {
            if (handler.Type is { } type && !byType.ContainsKey(type))
            {
                byType.Add(type, handler);
            }
        }

        _handlersByType = byType.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
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
        step.Inputs = InputResolutionPipeline.Resolve(step.Inputs, context.TriggerData, context.TriggerHeaders);

        // Pass 2 (async): resolve @steps('key').output|status|error expressions.
        // step.Key is the runtime key, so the resolver can rewrite a bare sibling reference
        // (e.g. @steps('wait_signal')) to the current loop scope when this step runs inside a ForEach.
        var resolver = new StepOutputResolver(_outputsRepository, _runStore, context.RunId, flow.Manifest.Steps, step.Key);
        step.Inputs = await StepExpressionResolutionPipeline.ResolveAsync(step.Inputs, resolver).ConfigureAwait(false);

        // Pass 3: inject the enclosing ForEach iteration context. Only the loop's ENTRY children
        // get __loopItem / __loopIndex baked in at fan-out; every other loop child is dispatched
        // from the loop's template metadata by the DAG continuation, signal resume, retry, or crash
        // recovery, and would otherwise see both keys missing. Deliberately runs AFTER the two
        // resolution passes so a loop item that happens to be a string starting with '@' is never
        // mistaken for an expression.
        // The key is parsed once and the resolved scope drives both the input injection and
        // IStepInstance.Index, which documents itself as the iteration index of the enclosing
        // scope but which no dispatch site ever assigned — every iteration read 0.
        if (LoopScopeInputs.TryResolveScope(step.Key, flow.Manifest.Steps, out var scope))
        {
            step.Inputs = LoopScopeInputs.Apply(
                step.Inputs,
                scope,
                context.TriggerData,
                context.TriggerHeaders);

            step.Index = scope.Index;
        }

        await _outputsRepository.SaveStepInputAsync(context, flow, step).ConfigureAwait(false);

        _handlersByType.TryGetValue(metadata.Type, out var handler);
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
}
