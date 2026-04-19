using System.Text.Json.Serialization;

namespace FlowOrchestrator.Core.Abstractions;

/// <summary>
/// Execution state of a step within a flow run.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepStatus
{
    /// <summary>The step has not yet started or is waiting to be rescheduled (polling).</summary>
    Pending = 0,

    /// <summary>The step is currently executing.</summary>
    Running = 1,

    /// <summary>The step completed without error.</summary>
    Succeeded = 2,

    /// <summary>The step threw an exception or returned a failed result.</summary>
    Failed = 3,

    /// <summary>
    /// The step was intentionally not executed because its <see cref="StepMetadata.RunAfter"/>
    /// conditions could not be satisfied (e.g. a dependency failed).
    /// </summary>
    Skipped = 4
}
