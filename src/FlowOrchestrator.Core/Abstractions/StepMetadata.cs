using System.Text.Json.Serialization;
using FlowOrchestrator.Core.Serialization;

namespace FlowOrchestrator.Core.Abstractions;

/// <summary>
/// Defines a single step in a flow manifest, specifying its handler type,
/// dependency declarations, and static input values.
/// </summary>
[JsonConverter(typeof(StepMetadataJsonConverter))]
public class StepMetadata
{
    /// <summary>
    /// Handler type name that maps to a registered step handler via
    /// <c>AddStepHandler&lt;T&gt;("TypeName")</c> in the DI container.
    /// </summary>
    public string Type { get; set; } = default!;

    /// <summary>
    /// Declares which preceding steps must reach a terminal status before this step
    /// is eligible to run. An empty collection means the step is an entry point
    /// and is enqueued immediately when the flow is triggered.
    /// </summary>
    public RunAfterCollection RunAfter { get; set; } = new();

    /// <summary>
    /// Static input values merged with runtime-resolved expressions before the
    /// handler receives them. Values may include <c>@triggerBody()</c> and
    /// <c>@triggerHeaders()</c> expressions resolved at execution time.
    /// </summary>
    public IDictionary<string, object?> Inputs { get; set; } = new Dictionary<string, object?>();
}
