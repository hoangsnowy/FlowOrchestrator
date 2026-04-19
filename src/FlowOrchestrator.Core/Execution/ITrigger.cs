namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Represents the event that started a flow run, providing the payload and metadata
/// that are persisted and made available to steps via expression resolution.
/// </summary>
public interface ITrigger
{
    /// <summary>The manifest key of the trigger definition that was fired (e.g. <c>"schedule"</c>, <c>"webhook"</c>).</summary>
    string Key { get; }

    /// <summary>The trigger type name (e.g. <c>"Cron"</c>, <c>"Manual"</c>, <c>"Webhook"</c>).</summary>
    string Type { get; }

    /// <summary>
    /// The trigger payload supplied at fire time (e.g. the parsed JSON body of a webhook request).
    /// Stored as trigger data for the run and accessible via <c>@triggerBody()</c> expressions.
    /// </summary>
    object? Data { get; }

    /// <summary>
    /// HTTP headers from the inbound request, or <see langword="null"/> for non-HTTP triggers.
    /// Accessible via <c>@triggerHeaders()</c> expressions.
    /// </summary>
    IReadOnlyDictionary<string, string>? Headers { get; }
}
