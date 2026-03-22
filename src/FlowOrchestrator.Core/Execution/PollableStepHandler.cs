using System.Globalization;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Base class for step handlers that support polling via Hangfire delayed jobs.
/// Subclasses implement <see cref="FetchAsync"/>; poll state tracking and retry scheduling are handled here.
/// </summary>
public abstract class PollableStepHandler<TInput> : IStepHandler<TInput>
    where TInput : IPollableInput
{
    public async ValueTask<object?> ExecuteAsync(
        IExecutionContext ctx, IFlowDefinition flow, IStepInstance<TInput> step)
    {
        var input = step.Inputs;
        var (result, parsedAsJson) = await FetchAsync(ctx, flow, step).ConfigureAwait(false);

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
            ResetPollState(input);
            return new StepResult<JsonElement> { Key = step.Key, Value = result };
        }

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
    /// Perform the actual data fetch. Returns the payload and whether it was parsed as JSON.
    /// </summary>
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
