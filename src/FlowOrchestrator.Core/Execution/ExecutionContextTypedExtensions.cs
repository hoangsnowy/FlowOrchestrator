using System.Text.Json;
using FlowOrchestrator.Core.Serialization;

namespace FlowOrchestrator.Core.Execution;

public static class ExecutionContextTypedExtensions
{
    public static T? GetTriggerDataAs<T>(this IExecutionContext context, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return JsonValueConversion.Deserialize<T>(context.TriggerData, options);
    }

    public static bool TryGetTriggerDataAs<T>(this IExecutionContext context, out T? value, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return JsonValueConversion.TryDeserialize(context.TriggerData, out value, options);
    }
}
