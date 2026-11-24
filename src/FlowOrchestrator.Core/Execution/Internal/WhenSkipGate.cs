using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Execution.Internal;

/// <summary>
/// Schedule-time gate for a ready step: evaluates its <c>When</c> clauses and, when one is false, records
/// the step <see cref="StepStatus.Skipped"/> under the step claim — the same decision, skip reason and
/// persisted trace the engine's graph continuation applies before it dispatches a successor.
/// </summary>
/// <remarks>
/// <para>
/// <c>When</c> is evaluated only when a step is SCHEDULED; <c>RunStepAsync</c> never re-checks it. Every
/// path that dispatches a ready step outside the continuation — startup recovery today — must therefore go
/// through this gate, or a host that died between a parent's completion and its continuation executes a
/// child whose <c>When</c> is false on restart.
/// </para>
/// <para>
/// The engine still carries its own copy (<c>FlowOrchestratorEngine.TryEvaluateWhenAndSkipAsync</c>), which
/// additionally emits the <c>flow.step.when</c> span and the skip counter. The two must keep the same skip
/// reason text and trace shape; folding the engine's copy onto this type is tracked as follow-up work.
/// </para>
/// </remarks>
internal sealed class WhenSkipGate
{
    private static readonly JsonSerializerOptions TraceJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WhenClauseEvaluator _evaluator;
    private readonly IFlowRunRuntimeStore _runtimeStore;
    private readonly ILogger _logger;

    /// <summary>Initialises the gate.</summary>
    /// <param name="evaluator">Evaluator that resolves the <c>When</c> expressions against the run's state.</param>
    /// <param name="runtimeStore">Store that owns the step claim and the skipped-status write.</param>
    /// <param name="logger">Logger for malformed-expression diagnostics.</param>
    public WhenSkipGate(WhenClauseEvaluator evaluator, IFlowRunRuntimeStore runtimeStore, ILogger logger)
    {
        _evaluator = evaluator;
        _runtimeStore = runtimeStore;
        _logger = logger;
    }

    /// <summary>
    /// Evaluates <paramref name="metadata"/>'s <c>When</c> clauses for <paramref name="stepKey"/> and records
    /// the step skipped when one of them is false.
    /// </summary>
    /// <param name="ctx">Execution context of the run (trigger body and headers feed the expressions).</param>
    /// <param name="flow">The flow being executed.</param>
    /// <param name="stepKey">The (runtime) key of the ready step.</param>
    /// <param name="metadata">The step's manifest metadata.</param>
    /// <returns>
    /// <see langword="true"/> when a <c>When</c> clause is false — the step must NOT be dispatched, whether
    /// this call recorded the skip or another worker already holds the step's claim; <see langword="false"/>
    /// when the step has no <c>When</c> clause or every clause is true.
    /// </returns>
    /// <remarks>
    /// A malformed expression is an authoring error and skips the step with a synthetic trace, exactly as the
    /// engine does, so the dashboard surfaces it. Cost: one evaluation (output reads per referenced step)
    /// plus, on a false clause, one claim and one status write.
    /// </remarks>
    public async ValueTask<bool> IsFalseAndSkippedAsync(IExecutionContext ctx, IFlowDefinition flow, string stepKey, StepMetadata metadata)
    {
        if (metadata.RunAfter.Count == 0 || !HasAnyWhen(metadata))
        {
            return false;
        }

        WhenEvaluationTrace? trace;
        try
        {
            trace = await _evaluator.EvaluateAsync(ctx, flow, metadata).ConfigureAwait(false);
        }
        catch (FlowExpressionException ex)
        {
            EngineLog.WhenEvaluationFailed(_logger, ex, stepKey);
            trace = new WhenEvaluationTrace
            {
                Expression = ex.Expression,
                Resolved = ex.Message,
                Result = false
            };
        }

        if (trace is null)
        {
            return false;
        }

        if (await _runtimeStore.TryClaimStepAsync(ctx.RunId, stepKey).ConfigureAwait(false))
        {
            var traceJson = JsonSerializer.Serialize(trace, TraceJsonOptions);
            var reason = $"When clause '{trace.Expression}' evaluated to false ({trace.Resolved}).";
            await _runtimeStore.RecordSkippedStepAsync(ctx.RunId, stepKey, metadata.Type, reason, traceJson).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Records <paramref name="stepKey"/> skipped because its <c>runAfter</c> prerequisites finished in a status
    /// the clause does not accept — the continuation's blocked-step pass.
    /// </summary>
    /// <param name="runId">The run the step belongs to.</param>
    /// <param name="stepKey">The (runtime) key of the blocked step.</param>
    /// <param name="stepType">The step's handler type name, stored with the skipped row.</param>
    /// <returns><see langword="true"/> when this call won the claim and recorded the skip.</returns>
    public async ValueTask<bool> TrySkipBlockedAsync(Guid runId, string stepKey, string stepType)
    {
        if (!await _runtimeStore.TryClaimStepAsync(runId, stepKey).ConfigureAwait(false))
        {
            return false;
        }

        await _runtimeStore.RecordSkippedStepAsync(runId, stepKey, stepType, StepSkipReasons.PrerequisitesUnmet).ConfigureAwait(false);
        return true;
    }

    // foreach rather than LINQ: RunAfter is small, but this runs once per ready step and a closure-free walk
    // allocates nothing.
    private static bool HasAnyWhen(StepMetadata metadata)
    {
        foreach (var condition in metadata.RunAfter.Values)
        {
            if (!string.IsNullOrWhiteSpace(condition.When))
            {
                return true;
            }
        }

        return false;
    }
}
