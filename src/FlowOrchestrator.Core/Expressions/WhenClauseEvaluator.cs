using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Expressions;

/// <summary>
/// Evaluates the <c>When</c> clauses on a step's <see cref="RunAfterCollection"/> entries
/// against the current run state, combining results with AND semantics.
/// </summary>
/// <remarks>
/// Reuses <see cref="StepOutputResolver"/> for <c>@steps()</c> resolution and
/// <see cref="TriggerExpressionResolver"/> for <c>@triggerBody()</c>/<c>@triggerHeaders()</c>.
/// </remarks>
internal sealed class WhenClauseEvaluator
{
    private readonly IOutputsRepository _outputsRepository;
    private readonly IFlowRunStore _runStore;
    private readonly BooleanExpressionEvaluator _evaluator = new();

    /// <summary>Initialises the evaluator with the storage dependencies needed for LHS resolution.</summary>
    public WhenClauseEvaluator(IOutputsRepository outputsRepository, IFlowRunStore runStore)
    {
        _outputsRepository = outputsRepository;
        _runStore = runStore;
    }

    /// <summary>
    /// Evaluates every <c>When</c> clause on <paramref name="metadata"/>'s <c>RunAfter</c> entries,
    /// resolving <c>@steps('key')</c> against the manifest's top level only.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when no <c>When</c> clauses are present or all evaluate to
    /// <see langword="true"/>; otherwise a <see cref="WhenEvaluationTrace"/> for the first
    /// clause that evaluated to <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Use <see cref="EvaluateAsync(IExecutionContext, IFlowDefinition, StepMetadata, string?)"/> for a
    /// step inside a loop body, so a bare sibling key resolves in the step's own iteration.
    /// </remarks>
    public ValueTask<WhenEvaluationTrace?> EvaluateAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        StepMetadata metadata)
        => EvaluateAsync(ctx, flow, metadata, currentStepKey: null);

    /// <summary>
    /// Evaluates every <c>When</c> clause on <paramref name="metadata"/>'s <c>RunAfter</c> entries for
    /// the step at runtime key <paramref name="currentStepKey"/>.
    /// </summary>
    /// <param name="ctx">Execution context of the run; supplies the run id and trigger payload.</param>
    /// <param name="flow">The flow whose manifest declares the step.</param>
    /// <param name="metadata">The step's metadata, carrying the <c>When</c> clauses.</param>
    /// <param name="currentStepKey">
    /// Runtime key of the step being gated (e.g. <c>"loop.0.check"</c>). A bare
    /// <c>@steps('sibling')</c> that is not a top-level step resolves in this key's loop scope
    /// (<c>"loop.0.sibling"</c>), exactly as it does for the step's inputs. <see langword="null"/>
    /// resolves against the top level only.
    /// </param>
    /// <returns>
    /// <see langword="null"/> when no <c>When</c> clauses are present or all evaluate to
    /// <see langword="true"/>; otherwise a <see cref="WhenEvaluationTrace"/> for the first
    /// clause that evaluated to <see langword="false"/>.
    /// </returns>
    public async ValueTask<WhenEvaluationTrace?> EvaluateAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        StepMetadata metadata,
        string? currentStepKey)
    {
        if (metadata.RunAfter.Count == 0)
        {
            return null;
        }

        BooleanExpressionEvaluator.LhsResolverAsync? resolver = null;

        foreach (var entry in metadata.RunAfter.Values)
        {
            if (string.IsNullOrWhiteSpace(entry.When))
            {
                continue;
            }

            resolver ??= BuildResolver(ctx, flow, currentStepKey);
            var trace = await _evaluator.EvaluateAsync(entry.When!, resolver).ConfigureAwait(false);
            if (!trace.Result)
            {
                return trace;
            }
        }

        return null;
    }

    private BooleanExpressionEvaluator.LhsResolverAsync BuildResolver(IExecutionContext ctx, IFlowDefinition flow, string? currentStepKey)
    {
        // Same scope-relative resolution DefaultStepExecutor applies to the step's inputs.
        var stepResolver = new StepOutputResolver(_outputsRepository, _runStore, ctx.RunId, flow.Manifest.Steps, currentStepKey);

        return async (lhs) =>
        {
            if (StepOutputResolver.IsStepExpression(lhs))
            {
                return await stepResolver.ResolveAsync(lhs).ConfigureAwait(false);
            }

            if (TriggerExpressionResolver.TryResolveTriggerBodyExpression(lhs, ctx.TriggerData, out var bodyValue))
            {
                return bodyValue;
            }

            if (TriggerExpressionResolver.TryResolveTriggerHeadersExpression(lhs, ctx.TriggerHeaders, out var headerValue))
            {
                return headerValue;
            }

            throw new FlowExpressionException(
                lhs,
                stepKey: string.Empty,
                $"Unknown LHS expression '{lhs}'. Expected '@steps(...)', '@triggerBody(...)', or '@triggerHeaders(...)'.");
        };
    }
}
