using System.Text.Json.Serialization;
using FlowOrchestrator.Core.Serialization;

namespace FlowOrchestrator.Core.Abstractions;

[JsonConverter(typeof(StepMetadataJsonConverter))]
public class StepMetadata
{
    public string Type { get; set; } = default!;

    public RunAfterCollection RunAfter { get; set; } = new();

    public IDictionary<string, object?> Inputs { get; set; } = new Dictionary<string, object?>();

    public virtual bool ShouldExecute(string precedingStepKey, string status)
    {
        if (RunAfter.Count == 0)
        {
            return true;
        }

        if (!RunAfter.TryGetValue(precedingStepKey, out var allowedStatuses) || allowedStatuses is null)
        {
            return false;
        }

        return allowedStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);
    }
}
