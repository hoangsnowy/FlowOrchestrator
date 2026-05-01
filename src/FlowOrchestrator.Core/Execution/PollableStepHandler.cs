using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Observability;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Base class for step handlers that need to repeatedly query an external system
/// until a condition is met, using Hangfire's delayed scheduling for pacing.
/// </summary>
/// <remarks>
/// Subclasses implement <see cref="FetchAsync"/> to perform the actual fetch.
/// The base class manages:
/// <list type="bullet">
///   <item>Tracking poll start time and attempt count in the step inputs (persisted between reschedules).</item>
///   <item>Evaluating <see cref="IPollableInput.PollConditionPath"/> against the response.</item>
///   <item>Enforcing <see cref="IPollableInput.PollMinAttempts"/> before accepting a positive condition.</item>
///   <item>Returning <see cref="StepStatus.Pending"/> with <see cref="IStepResult.DelayNextStep"/> to reschedule.</item>
///   <item>Returning <see cref="StepStatus.Failed"/> on timeout.</item>
/// </list>
/// </remarks>
/// <typeparam name="TInput">Step input type implementing <see cref="IPollableInput"/>.</typeparam>
public abstract class PollableStepHandler<TInput> : IStepHandler<TInput>
    where TInput : IPollableInput
{
    /// <inheritdoc/>
    public async ValueTask<object?> ExecuteAsync(
        IExecutionContext ctx, IFlowDefinition flow, IStepInstance<TInput> step)
    {
        var input = step.Inputs;

        // Open a span around each individual poll attempt so APMs show the polling history as a
        // sequence of child spans of the parent flow.step. The current attempt number is read
        // before IncrementPollAttempt so it reflects "the attempt about to happen".
        using var activity = FlowOrchestratorTelemetry.SharedActivitySource.StartActivity(
            "flow.step.poll",
            ActivityKind.Internal);
        activity?.SetTag("flow.id", flow.Id.ToString());
        activity?.SetTag("run.id", ctx.RunId.ToString());
        activity?.SetTag("step.key", step.Key);
        activity?.SetTag("flow.poll.attempt", (input.PollAttempt ?? 0) + 1);

        (JsonElement result, bool parsedAsJson) fetched;
        try
        {
            fetched = await FetchAsync(ctx, flow, step).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
        var (result, parsedAsJson) = fetched;

        if (!input.PollEnabled)
        {
            return new StepResult<JsonElement> { Key = step.Key, Value = result };
        }

        if (!parsedAsJson && !string.IsNullOrWhiteSpace(input.PollConditionPath))
        {
            ResetPollState(input);
            return new StepResult<JsonElement>
            {
                Key = step.Key,
                Status = StepStatus.Failed,
                FailedReason = "Polling with 'pollConditionPath' requires a JSON response body.",
                Value = result
            };
        }

        var intervalSeconds = Math.Max(1, input.PollIntervalSeconds);
        var timeoutSeconds = Math.Max(intervalSeconds, input.PollTimeoutSeconds);
        var minAttempts = Math.Max(1, input.PollMinAttempts);
        var pollStartedAt = GetPollStartedAt(input);
        var currentAttempt = IncrementPollAttempt(input);

        if (currentAttempt >= minAttempts
            && PollConditionEvaluator.IsMatched(result, input.PollConditionPath, input.PollConditionEquals))
        {
            activity?.SetTag("flow.poll.condition_met", true);
            ResetPollState(input);
            return new StepResult<JsonElement> { Key = step.Key, Value = result };
        }
        activity?.SetTag("flow.poll.condition_met", false);

        var elapsed = DateTimeOffset.UtcNow - pollStartedAt;
        if (elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
        {
            ResetPollState(input);
            return new StepResult<JsonElement>
            {
                Key = step.Key,
                Status = StepStatus.Failed,
                FailedReason = $"Polling timed out after {timeoutSeconds} seconds.",
                Value = result
            };
        }

        return new StepResult<JsonElement>
        {
            Key = step.Key,
            Status = StepStatus.Pending,
            DelayNextStep = TimeSpan.FromSeconds(intervalSeconds),
            Value = result
        };
    }

    /// <summary>
    /// Performs the actual data fetch against the external system.
    /// </summary>
    /// <returns>
    /// A tuple of the fetched <see cref="JsonElement"/> payload and a flag indicating
    /// whether the response was parsed as valid JSON (required for <see cref="IPollableInput.PollConditionPath"/> evaluation).
    /// </returns>
    protected abstract ValueTask<(JsonElement Result, bool IsJson)> FetchAsync(
        IExecutionContext ctx, IFlowDefinition flow, IStepInstance<TInput> step);

    private static DateTimeOffset GetPollStartedAt(TInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.PollStartedAtUtc)
            && DateTimeOffset.TryParse(input.PollStartedAtUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        var now = DateTimeOffset.UtcNow;
        input.PollStartedAtUtc = now.ToString("O", CultureInfo.InvariantCulture);
        return now;
    }

    private static int IncrementPollAttempt(TInput input)
    {
        var next = (input.PollAttempt ?? 0) + 1;
        input.PollAttempt = next;
        return next;
    }

    private static void ResetPollState(TInput input)
    {
        input.PollStartedAtUtc = null;
        input.PollAttempt = null;
    }
}
