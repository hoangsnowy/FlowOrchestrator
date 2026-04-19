using System.Text.Json;
using FlowOrchestrator.Core.Serialization;

namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Extension methods for retrieving strongly-typed step outputs and trigger data
/// from an <see cref="IOutputsRepository"/>.
/// </summary>
public static class OutputsRepositoryTypedExtensions
{
    /// <summary>
    /// Retrieves and deserialises the trigger data for the given run to <typeparamref name="T"/>.
    /// Returns <see langword="default"/> when no trigger data was stored or conversion fails.
    /// </summary>
    /// <typeparam name="T">The target CLR type to deserialise to.</typeparam>
    /// <param name="outputsRepository">The repository to read from.</param>
    /// <param name="runId">The run whose trigger data is requested.</param>
    /// <param name="options">Optional JSON serialiser options. Uses web defaults when omitted.</param>
    public static async ValueTask<T?> GetTriggerDataAsync<T>(
        this IOutputsRepository outputsRepository,
        Guid runId,
        JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(outputsRepository);
        var value = await outputsRepository.GetTriggerDataAsync(runId).ConfigureAwait(false);
        return JsonValueConversion.Deserialize<T>(value, options);
    }

    /// <summary>
    /// Retrieves and deserialises the output of the step identified by <paramref name="stepKey"/>
    /// within the given run to <typeparamref name="T"/>.
    /// Returns <see langword="default"/> when the step has not completed or conversion fails.
    /// </summary>
    /// <typeparam name="T">The target CLR type to deserialise to.</typeparam>
    /// <param name="outputsRepository">The repository to read from.</param>
    /// <param name="runId">The run that owns the step.</param>
    /// <param name="stepKey">The step key within the flow manifest.</param>
    /// <param name="options">Optional JSON serialiser options.</param>
    public static async ValueTask<T?> GetStepOutputAsync<T>(
        this IOutputsRepository outputsRepository,
        Guid runId,
        string stepKey,
        JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(outputsRepository);
        var value = await outputsRepository.GetStepOutputAsync(runId, stepKey).ConfigureAwait(false);
        return JsonValueConversion.Deserialize<T>(value, options);
    }
}
