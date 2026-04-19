using System.Text.Json;
using FlowOrchestrator.Core.Serialization;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Extension methods for retrieving strongly-typed trigger data from an <see cref="IExecutionContext"/>.
/// </summary>
public static class ExecutionContextTypedExtensions
{
    /// <summary>
    /// Deserialises <see cref="IExecutionContext.TriggerData"/> to <typeparamref name="T"/>.
    /// Returns <see langword="default"/> when the data is <see langword="null"/> or cannot be converted.
    /// </summary>
    /// <typeparam name="T">The target CLR type to deserialise to.</typeparam>
    /// <param name="context">The execution context whose trigger data is read.</param>
    /// <param name="options">Optional JSON serialiser options. Uses web defaults when omitted.</param>
    public static T? GetTriggerDataAs<T>(this IExecutionContext context, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return JsonValueConversion.Deserialize<T>(context.TriggerData, options);
    }

    /// <summary>
    /// Tries to deserialise <see cref="IExecutionContext.TriggerData"/> to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The target CLR type.</typeparam>
    /// <param name="context">The execution context whose trigger data is read.</param>
    /// <param name="value">The deserialised value, or <see langword="default"/> on failure.</param>
    /// <param name="options">Optional JSON serialiser options.</param>
    /// <returns><see langword="true"/> if deserialisation succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryGetTriggerDataAs<T>(this IExecutionContext context, out T? value, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return JsonValueConversion.TryDeserialize(context.TriggerData, out value, options);
    }
}
