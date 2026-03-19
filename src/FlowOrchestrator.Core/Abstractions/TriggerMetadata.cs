using System.Text.Json.Serialization;

namespace FlowOrchestrator.Core.Abstractions;

public sealed class TriggerMetadata
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TriggerType Type { get; set; }
    public IDictionary<string, object?> Inputs { get; set; } = new Dictionary<string, object?>();

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
