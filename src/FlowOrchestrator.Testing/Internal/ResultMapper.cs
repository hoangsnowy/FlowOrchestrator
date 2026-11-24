using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Testing.Internal;

/// <summary>
/// Maps storage records (<see cref="FlowRunRecord"/> + <see cref="FlowStepRecord"/>) to the
/// public test-result shapes (<see cref="FlowTestRunResult"/> + <see cref="FlowTestStepResult"/>).
/// </summary>
internal static class ResultMapper
{
    private static readonly JsonElement NullElement = JsonDocument.Parse("null").RootElement.Clone();

    public static FlowTestRunResult Map(
        FlowRunRecord run,
        TimeSpan duration,
        bool timedOut)
    {
        var steps = new Dictionary<string, FlowTestStepResult>(StringComparer.Ordinal);
        if (run.Steps is not null)
        {
            foreach (var step in run.Steps)
            {
                steps[step.StepKey] = MapStep(step);
            }
        }

        return new FlowTestRunResult
        {
            RunId = run.Id,
            Status = ParseRunStatus(run.Status),
            Steps = steps,
            Events = FlowRunTimeline.Build(run),
            Duration = duration,
            TimedOut = timedOut
        };
    }

    public static FlowTestStepResult MapStep(FlowStepRecord step) => new()
    {
        Status = ParseStepStatus(step.Status),
        Output = ParseJson(step.OutputJson),
        Inputs = ParseJson(step.InputJson),
        FailureReason = step.ErrorMessage,
        AttemptCount = step.AttemptCount,
        StartedAt = step.StartedAt,
        CompletedAt = step.CompletedAt
    };

    public static RunStatus ParseRunStatus(string? raw) => raw switch
    {
        RunStatusNames.Succeeded => RunStatus.Succeeded,
        RunStatusNames.Failed => RunStatus.Failed,
        RunStatusNames.Cancelled => RunStatus.Cancelled,
        RunStatusNames.TimedOut => RunStatus.TimedOut,
        RunStatusNames.Skipped => RunStatus.Skipped,
        _ => RunStatus.Running
    };

    public static StepStatus ParseStepStatus(string? raw) =>
        Enum.TryParse<StepStatus>(raw, ignoreCase: true, out var parsed) ? parsed : StepStatus.Pending;

    /// <summary>Every run status the engine persists as final (<see cref="RunStatusNames"/>), Skipped included.</summary>
    public static bool IsTerminal(string? status) =>
        status is RunStatusNames.Succeeded or RunStatusNames.Failed or RunStatusNames.Cancelled
            or RunStatusNames.TimedOut or RunStatusNames.Skipped;

    private static JsonElement ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return NullElement;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return NullElement;
        }
    }
}
