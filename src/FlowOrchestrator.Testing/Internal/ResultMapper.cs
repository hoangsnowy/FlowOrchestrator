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
        IReadOnlyList<FlowEventRecord> events,
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
            Events = events,
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
        "Succeeded" => RunStatus.Succeeded,
        "Failed" => RunStatus.Failed,
        "Cancelled" => RunStatus.Cancelled,
        "TimedOut" => RunStatus.TimedOut,
        _ => RunStatus.Running
    };

    public static StepStatus ParseStepStatus(string? raw) =>
        Enum.TryParse<StepStatus>(raw, ignoreCase: true, out var parsed) ? parsed : StepStatus.Pending;

    public static bool IsTerminal(string? status) =>
        status is "Succeeded" or "Failed" or "Cancelled" or "TimedOut";

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
