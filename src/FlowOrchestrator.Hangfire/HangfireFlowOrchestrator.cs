using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Hangfire;

public sealed class HangfireFlowOrchestrator : IHangfireFlowTrigger, IHangfireStepRunner
{
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly IFlowExecutor _flowExecutor;
    private readonly IStepExecutor _stepExecutor;
    private readonly IFlowRunStore _runStore;
    private readonly IOutputsRepository _outputsRepository;
    private readonly IExecutionContextAccessor _contextAccessor;
    private readonly IFlowRepository _flowRepository;
    private readonly ILogger<HangfireFlowOrchestrator> _logger;

    public HangfireFlowOrchestrator(
        IBackgroundJobClient backgroundJobClient,
        IFlowExecutor flowExecutor,
        IStepExecutor stepExecutor,
        IFlowRunStore runStore,
        IOutputsRepository outputsRepository,
        IExecutionContextAccessor contextAccessor,
        IFlowRepository flowRepository,
        ILogger<HangfireFlowOrchestrator> logger)
    {
        _backgroundJobClient = backgroundJobClient;
        _flowExecutor = flowExecutor;
        _stepExecutor = stepExecutor;
        _runStore = runStore;
        _outputsRepository = outputsRepository;
        _contextAccessor = contextAccessor;
        _flowRepository = flowRepository;
        _logger = logger;
    }

    // Serializes an object? safely, handling JsonElement values that may be default/undefined
    // or backed by a disposed JsonDocument, which would otherwise throw InvalidOperationException.
    private static string? SafeSerialize(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement element)
            return element.ValueKind == JsonValueKind.Undefined ? null : element.GetRawText();
        return JsonSerializer.Serialize(value);
    }

    private async ValueTask EnsureTriggerDataAsync(IExecutionContext ctx)
    {
        // Prefer the repository as the authoritative source. When RunStepAsync is
        // invoked as a Hangfire background job, ctx.TriggerData may have been
        // corrupted during Hangfire (de)serialization (e.g. JsonElement → JObject),
        // so a non-null value on ctx is not a reliable signal that the data is valid.
        var fromRepo = await _outputsRepository.GetTriggerDataAsync(ctx.RunId).ConfigureAwait(false);
        if (fromRepo is not null)
        {
            ctx.TriggerData = fromRepo;
        }
        else if (ctx.TriggerData is null)
        {
            if (ctx is ITriggerContext triggerContext && triggerContext.Trigger is not null)
            {
                ctx.TriggerData = triggerContext.Trigger.Data;
            }
        }

        if (ctx.TriggerHeaders is null)
        {
            ctx.TriggerHeaders = await _outputsRepository.GetTriggerHeadersAsync(ctx.RunId).ConfigureAwait(false);
        }
    }

    public async ValueTask<object?> TriggerAsync(ITriggerContext triggerContext, PerformContext? performContext = null)
    {
        triggerContext.RunId = triggerContext.RunId == Guid.Empty ? Guid.NewGuid() : triggerContext.RunId;
        triggerContext.TriggerData = triggerContext.Trigger.Data;
        triggerContext.TriggerHeaders = triggerContext.Trigger.Headers;
        _contextAccessor.CurrentContext = triggerContext;

        try
        {
            var triggerDataJson = SafeSerialize(triggerContext.Trigger.Data);

            try
            {
                await _runStore.StartRunAsync(
                    triggerContext.Flow.Id,
                    triggerContext.Flow.GetType().Name,
                    triggerContext.RunId,
                    triggerContext.Trigger.Key,
                    triggerDataJson,
                    performContext?.BackgroundJob?.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to track flow run start.");
            }

            var firstStep = await _flowExecutor.TriggerFlow(triggerContext).ConfigureAwait(false);

            _backgroundJobClient.Enqueue<IHangfireStepRunner>(
                runner => runner.RunStepAsync(triggerContext, triggerContext.Flow, firstStep, null));

            return null;
        }
        finally
        {
            _contextAccessor.CurrentContext = null;
        }
    }

    public async ValueTask<object?> RunStepAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, PerformContext? performContext = null)
    {
        await EnsureTriggerDataAsync(ctx).ConfigureAwait(false);
        _contextAccessor.CurrentContext = ctx;

        try
        {
            string? inputJson = null;
            try
            {
                await _outputsRepository.SaveStepInputAsync(ctx, flow, step).ConfigureAwait(false);
                inputJson = SafeSerialize(step.Inputs);
                await _runStore.RecordStepStartAsync(ctx.RunId, step.Key, step.Type, inputJson, performContext?.BackgroundJob?.Id)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to track step start.");
            }

            IStepResult result;
            try
            {
                result = await _stepExecutor.ExecuteAsync(ctx, flow, step).ConfigureAwait(false);
                await _outputsRepository.SaveStepOutputAsync(ctx, flow, step, result).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Step execution failed for {StepKey}", step.Key);
                result = new StepResult
                {
                    Key = step.Key,
                    Status = StepStatus.Failed,
                    FailedReason = ex.ToString(),
                    ReThrow = false
                };
            }

            try
            {
                var outputJson = SafeSerialize(result.Result);
                await _runStore.RecordStepCompleteAsync(ctx.RunId, step.Key, result.Status.ToString(), outputJson, result.FailedReason)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to track step completion.");
            }

            if (result.Status == StepStatus.Pending)
            {
                var retryDelay = result.DelayNextStep ?? TimeSpan.FromSeconds(10);

                _backgroundJobClient.Schedule<IHangfireStepRunner>(
                    runner => runner.RunStepAsync(ctx, flow, step, null),
                    retryDelay);

                return result.Result;
            }

            var next = await _flowExecutor.GetNextStep(ctx, flow, step, result).ConfigureAwait(false);
            if (next is not null)
            {
                if (result.DelayNextStep.HasValue)
                {
                    _backgroundJobClient.Schedule<IHangfireStepRunner>(
                        runner => runner.RunStepAsync(ctx, flow, next, null),
                        result.DelayNextStep.Value);
                }
                else
                {
                    _backgroundJobClient.Enqueue<IHangfireStepRunner>(
                        runner => runner.RunStepAsync(ctx, flow, next, null));
                }
            }
            else
            {
                try
                {
                    await _runStore.CompleteRunAsync(ctx.RunId, result.Status.ToString()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to track run completion.");
                }
            }

            if (result.ReThrow)
            {
                throw new InvalidOperationException(result.FailedReason ?? "Step execution requested rethrow.");
            }

            return result.Result;
        }
        finally
        {
            _contextAccessor.CurrentContext = null;
        }
    }

    public async ValueTask<object?> TriggerByScheduleAsync(Guid flowId, string triggerKey, PerformContext? performContext = null)
    {
        var flows = await _flowRepository.GetAllFlowsAsync().ConfigureAwait(false);
        var flow = flows.FirstOrDefault(f => f.Id == flowId)
            ?? throw new InvalidOperationException($"Flow {flowId} not found.");

        var ctx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger(triggerKey, "Cron", null)
        };

        return await TriggerAsync(ctx, performContext).ConfigureAwait(false);
    }

    public async ValueTask<object?> RetryStepAsync(Guid flowId, Guid runId, string stepKey, PerformContext? performContext = null)
    {
        var flows = await _flowRepository.GetAllFlowsAsync().ConfigureAwait(false);
        var flow = flows.FirstOrDefault(f => f.Id == flowId)
            ?? throw new InvalidOperationException($"Flow {flowId} not found.");

        var stepMeta = flow.Manifest.Steps.FindStep(stepKey)
            ?? throw new InvalidOperationException($"Step '{stepKey}' not found in flow manifest.");

        await _runStore.RetryStepAsync(runId, stepKey).ConfigureAwait(false);

        var step = new StepInstance(stepKey, stepMeta.Type)
        {
            RunId = runId,
            ScheduledTime = DateTimeOffset.UtcNow,
            Inputs = new Dictionary<string, object?>(stepMeta.Inputs)
        };

        var ctx = new Core.Execution.ExecutionContext { RunId = runId };
        ctx.TriggerData = await _outputsRepository.GetTriggerDataAsync(runId).ConfigureAwait(false);

        return await RunStepAsync(ctx, flow, step, performContext).ConfigureAwait(false);
    }
}

