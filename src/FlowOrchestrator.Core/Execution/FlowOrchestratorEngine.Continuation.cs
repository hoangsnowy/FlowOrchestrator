using System.Diagnostics;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// DAG-continuation partial of <see cref="FlowOrchestratorEngine"/>: legacy sequential
/// next-step resolution, full-graph evaluation with When-skip propagation, and the
/// terminal-status decision via <see cref="RunTerminationClassifier"/>.
/// </summary>
public sealed partial class FlowOrchestratorEngine
{
    private async Task RunLegacySequentialContinuationAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, IStepResult result)
    {
        var next = await _flowExecutor.GetNextStep(ctx, flow, step, result).ConfigureAwait(false);
        if (next is not null)
        {
            await TryScheduleStepAsync(ctx, flow, next, result.DelayNextStep).ConfigureAwait(false);
            return;
        }

        await _runStore.CompleteRunAsync(ctx.RunId, result.Status.ToString()).ConfigureAwait(false);
        await RecordEventAsync(
            ctx,
            flow,
            step,
            "run.completed",
            $"Run completed with status {result.Status}.").ConfigureAwait(false);
    }

    private async Task RunGraphContinuationAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, IStepResult result)
    {
        var statuses = await _runtimeStore!.GetStepStatusesAsync(ctx.RunId).ConfigureAwait(false);
        var evaluation = _graphPlanner.Evaluate(flow, statuses);

        foreach (var blockedStepKey in evaluation.BlockedStepKeys)
        {
            if (!await _runtimeStore.TryClaimStepAsync(ctx.RunId, blockedStepKey).ConfigureAwait(false))
            {
                continue;
            }

            var metadata = flow.Manifest.Steps.FindStep(blockedStepKey);
            await _runtimeStore.RecordSkippedStepAsync(
                ctx.RunId,
                blockedStepKey,
                metadata?.Type ?? "Unknown",
                StepSkipReasons.PrerequisitesUnmet).ConfigureAwait(false);

            if (_observabilityOptions.EnableOpenTelemetry)
            {
                _telemetry.StepSkippedCounter.Add(
                    1,
                    new KeyValuePair<string, object?>("flow_id", flow.Id.ToString()),
                    new KeyValuePair<string, object?>("step_key", blockedStepKey),
                    new KeyValuePair<string, object?>("reason", "prerequisites_unmet"));
            }

            await RecordEventAsync(
                ctx,
                flow,
                step,
                "step.skipped",
                $"Step '{blockedStepKey}' skipped because dependencies did not match.",
                blockedStepKey).ConfigureAwait(false);
        }

        statuses = await _runtimeStore.GetStepStatusesAsync(ctx.RunId).ConfigureAwait(false);
        evaluation = _graphPlanner.Evaluate(flow, statuses);

        var termination = await ResolveTerminationStatusAsync(ctx.RunId).ConfigureAwait(false);
        var enqueued = 0;
        if (termination is null)
        {
            // When-evaluation may skip steps and unblock new dependents; loop until the
            // ready set is stable (no further skips produced).
            var safetyCounter = 0;
            while (true)
            {
                var anyWhenSkipped = false;
                foreach (var readyStepKey in evaluation.ReadyStepKeys)
                {
                    var metadata = flow.Manifest.Steps.FindStep(readyStepKey);
                    if (metadata is null)
                    {
                        continue;
                    }

                    if (await TryEvaluateWhenAndSkipAsync(ctx, flow, readyStepKey).ConfigureAwait(false))
                    {
                        anyWhenSkipped = true;
                        continue;
                    }

                    var nextStep = new StepInstance(readyStepKey, metadata.Type)
                    {
                        RunId = ctx.RunId,
                        PrincipalId = ctx.PrincipalId,
                        TriggerData = ctx.TriggerData,
                        TriggerHeaders = ctx.TriggerHeaders,
                        ScheduledTime = DateTimeOffset.UtcNow,
                        Inputs = new Dictionary<string, object?>(metadata.Inputs)
                    };

                    if (await TryScheduleStepAsync(ctx, flow, nextStep, result.DelayNextStep).ConfigureAwait(false))
                    {
                        enqueued++;
                    }
                }

                if (!anyWhenSkipped) break;
                if (++safetyCounter > flow.Manifest.Steps.Count + 4) break;

                statuses = await _runtimeStore.GetStepStatusesAsync(ctx.RunId).ConfigureAwait(false);
                evaluation = _graphPlanner.Evaluate(flow, statuses);
            }
        }

        if (enqueued > 0)
        {
            return;
        }

        statuses = await _runtimeStore.GetStepStatusesAsync(ctx.RunId).ConfigureAwait(false);
        if (statuses.Values.Any(IsInFlight))
        {
            return;
        }

        var claimed = await _runtimeStore.GetClaimedStepKeysAsync(ctx.RunId).ConfigureAwait(false);
        if (claimed.Except(statuses.Keys, StringComparer.Ordinal).Any())
        {
            return;
        }

        // Single source of truth — same classifier that FlowRunRecoveryHostedService uses.
        termination ??= RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        await TryCompleteRunAsync(ctx.RunId, termination).ConfigureAwait(false);
        await RecordEventAsync(
            ctx,
            flow,
            step,
            "run.completed",
            $"Run completed with status {termination}.").ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluates the <c>When</c> clauses on <paramref name="stepKey"/>'s metadata. If any
    /// clause returns <see langword="false"/>, marks the step <see cref="StepStatus.Skipped"/>
    /// and persists the evaluation trace for dashboard display.
    /// </summary>
    /// <returns><see langword="true"/> when the step was skipped due to a false When clause.</returns>
    private async Task<bool> TryEvaluateWhenAndSkipAsync(IExecutionContext ctx, IFlowDefinition flow, string stepKey)
    {
        var metadata = flow.Manifest.Steps.FindStep(stepKey);
        if (metadata is null || metadata.RunAfter.Count == 0)
        {
            return false;
        }

        var hasAnyWhen = metadata.RunAfter.Values.Any(v => !string.IsNullOrWhiteSpace(v.When));
        if (!hasAnyWhen)
        {
            return false;
        }

        using var whenActivity = _observabilityOptions.EnableOpenTelemetry
            ? _telemetry.ActivitySource.StartActivity("flow.step.when", ActivityKind.Internal)
            : null;
        whenActivity?.SetTag("flow.id", flow.Id.ToString());
        whenActivity?.SetTag("run.id", ctx.RunId.ToString());
        whenActivity?.SetTag("step.key", stepKey);

        WhenEvaluationTrace? trace;
        try
        {
            trace = await _whenEvaluator.EvaluateAsync(ctx, flow, metadata).ConfigureAwait(false);
        }
        catch (FlowExpressionException ex)
        {
            EngineLog.WhenEvaluationFailed(_logger, ex, stepKey);
            // Surface to the parent flow.step activity (if any) so APMs see a failure event.
            Activity.Current?.RecordError(ex);
            // A malformed expression is an authoring error. Skip with a synthetic trace
            // so the dashboard surfaces the problem to the user.
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

        if (_runtimeStore is null)
        {
            return false;
        }

        if (!await _runtimeStore.TryClaimStepAsync(ctx.RunId, stepKey).ConfigureAwait(false))
        {
            return false;
        }

        var traceJson = JsonSerializer.Serialize(trace, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var reason = $"When clause '{trace.Expression}' evaluated to false ({trace.Resolved}).";
        await _runtimeStore.RecordSkippedStepAsync(ctx.RunId, stepKey, metadata.Type, reason, traceJson).ConfigureAwait(false);

        whenActivity?.SetTag("flow.when.expression", trace.Expression);
        whenActivity?.SetTag("flow.when.resolved", trace.Resolved);
        whenActivity?.SetTag("flow.when.result", false);
        if (_observabilityOptions.EnableOpenTelemetry)
        {
            _telemetry.StepSkippedCounter.Add(
                1,
                new KeyValuePair<string, object?>("flow_id", flow.Id.ToString()),
                new KeyValuePair<string, object?>("step_key", stepKey),
                new KeyValuePair<string, object?>("reason", "when_false"));
        }

        await RecordEventAsync(
            ctx,
            flow,
            new StepInstance(stepKey, metadata.Type) { RunId = ctx.RunId, ScheduledTime = DateTimeOffset.UtcNow },
            "step.skipped",
            $"Step '{stepKey}' skipped because '{trace.Expression}' evaluated to false.",
            stepKey).ConfigureAwait(false);

        return true;
    }
}
