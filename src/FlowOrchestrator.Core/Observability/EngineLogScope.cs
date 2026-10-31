using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Observability;

/// <summary>
/// Helper for opening an <see cref="ILogger.BeginScope{TState}(TState)"/> populated with the
/// engine's standard correlation properties (<c>RunId</c>, <c>FlowId</c>, <c>StepKey</c>, <c>Attempt</c>).
/// </summary>
/// <remarks>
/// Every public engine entry point should wrap its body in <c>using var _ = EngineLogScope.Begin(...)</c>
/// so every nested log line — including logs emitted by user-written step handlers — automatically
/// carries the run-level correlation. Logging providers that honour scopes (Serilog, NLog, OpenTelemetry
/// Logs, Application Insights) surface these as searchable properties.
/// </remarks>
public static class EngineLogScope
{
    /// <summary>
    /// Begins a logger scope tagged with the run-level correlation IDs.
    /// </summary>
    /// <param name="logger">The logger to attach the scope to. Returns <see langword="null"/> when null.</param>
    /// <param name="runId">The current run identifier.</param>
    /// <param name="flowId">The flow identifier owning the run.</param>
    /// <param name="stepKey">Optional step key — set when scoped to a single step's execution.</param>
    /// <param name="attempt">Optional attempt number — set on retry execution paths.</param>
    /// <returns>An <see cref="IDisposable"/> that closes the scope, or <see langword="null"/> if the logger is null.</returns>
    public static IDisposable? Begin(
        ILogger? logger,
        Guid runId,
        Guid flowId,
        string? stepKey = null,
        int? attempt = null)
        => Begin(logger, runId, flowId, stepKey, attempt, flowName: null);

    /// <summary>
    /// Begins a logger scope tagged with the run-level correlation IDs plus human-readable names, so log
    /// backends can be filtered by flow, step type or trigger instead of by GUID.
    /// </summary>
    /// <param name="logger">The logger to attach the scope to. Returns <see langword="null"/> when null.</param>
    /// <param name="runId">The current run identifier.</param>
    /// <param name="flowId">The flow identifier owning the run.</param>
    /// <param name="stepKey">Optional step key — set when scoped to a single step's execution.</param>
    /// <param name="attempt">Optional attempt number — set on retry execution paths.</param>
    /// <param name="flowName">Flow name (the flow class name) — emitted as <c>FlowName</c> when set.</param>
    /// <param name="stepType">Step handler type — emitted as <c>StepType</c> when set.</param>
    /// <param name="triggerKey">Trigger key from the manifest — emitted as <c>TriggerKey</c> when set.</param>
    /// <returns>An <see cref="IDisposable"/> that closes the scope, or <see langword="null"/> if the logger is null.</returns>
    /// <remarks>
    /// One dictionary per engine entry point (trigger, step, retry), not per log line. String values are
    /// stripped of CR / LF so a manifest-supplied name cannot forge log lines in plain-text sinks.
    /// </remarks>
    public static IDisposable? Begin(
        ILogger? logger,
        Guid runId,
        Guid flowId,
        string? stepKey,
        int? attempt,
        string? flowName,
        string? stepType = null,
        string? triggerKey = null)
    {
        if (logger is null)
        {
            return null;
        }

        var state = new Dictionary<string, object?>(capacity: 7)
        {
            ["RunId"] = runId,
            ["FlowId"] = flowId,
        };
        if (!string.IsNullOrEmpty(stepKey))
        {
            state["StepKey"] = Sanitize(stepKey);
        }
        if (attempt.HasValue)
        {
            state["Attempt"] = attempt.Value;
        }
        if (!string.IsNullOrEmpty(flowName))
        {
            state["FlowName"] = Sanitize(flowName);
        }
        if (!string.IsNullOrEmpty(stepType))
        {
            state["StepType"] = Sanitize(stepType);
        }
        if (!string.IsNullOrEmpty(triggerKey))
        {
            state["TriggerKey"] = Sanitize(triggerKey);
        }

        return logger.BeginScope(state);
    }

    private static string Sanitize(string value) => value.Replace('\r', '_').Replace('\n', '_');
}
