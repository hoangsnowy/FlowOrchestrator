using System.Text.Json.Serialization;

namespace FlowOrchestrator.Core.Abstractions;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4
}
