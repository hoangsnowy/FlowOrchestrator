using System.Text.Json.Serialization;

namespace FlowOrchestrator.Core.Abstractions;

/// <summary>
/// Defines how a flow can be started — its trigger type and associated configuration inputs.
/// </summary>
public sealed class TriggerMetadata
{
    /// <summary>The mechanism that fires this trigger.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TriggerType Type { get; set; }

    /// <summary>
    /// Type-specific configuration values (e.g. <c>cronExpression</c> for <see cref="TriggerType.Cron"/>,
    /// <c>secret</c> for <see cref="TriggerType.Webhook"/>).
    /// </summary>
    public IDictionary<string, object?> Inputs { get; set; } = new Dictionary<string, object?>();

    /// <summary>
    /// Extracts the <c>cronExpression</c> input when this trigger is of type <see cref="TriggerType.Cron"/>.
    /// </summary>
    /// <param name="cronExpression">
    /// The raw cron expression (e.g. <c>"0 * * * *"</c>) if found; otherwise <see cref="string.Empty"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the trigger is a cron trigger and a non-empty expression exists;
    /// <see langword="false"/> otherwise.
    /// </returns>
    public bool TryGetCronExpression(out string cronExpression)
    {
        cronExpression = string.Empty;

        if (Type != TriggerType.Cron)
        {
            return false;
        }

        return Inputs.TryGetString("cronExpression", out cronExpression);
    }
}
