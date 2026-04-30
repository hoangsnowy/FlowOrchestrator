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
public sealed class WhenClauseEvaluator
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
    /// Evaluates every <c>When</c> clause on <paramref name="metadata"/>'s <c>RunAfter</c> entries.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when no <c>When</c> clauses are present or all evaluate to
    /// <see langword="true"/>; otherwise a <see cref="WhenEvaluationTrace"/> for the first
    /// clause that evaluated to <see langword="false"/>.
    /// </returns>
    public async ValueTask<WhenEvaluationTrace?> EvaluateAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        StepMetadata metadata)
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

            resolver ??= BuildResolver(ctx, flow);
            var trace = await _evaluator.EvaluateAsync(entry.When!, resolver).ConfigureAwait(false);
            if (!trace.Result)
            {
                return trace;
            }
        }

        return null;
    }

    private BooleanExpressionEvaluator.LhsResolverAsync BuildResolver(IExecutionContext ctx, IFlowDefinition flow)
    {
        var stepResolver = new StepOutputResolver(_outputsRepository, _runStore, ctx.RunId, flow.Manifest.Steps);

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
