using System.Text.Json;
using FlowOrchestrator.Core.Serialization;

namespace FlowOrchestrator.Core.Storage;

public static class OutputsRepositoryTypedExtensions
{
    public static async ValueTask<T?> GetTriggerDataAsync<T>(
        this IOutputsRepository outputsRepository,
        Guid runId,
        JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(outputsRepository);
        var value = await outputsRepository.GetTriggerDataAsync(runId).ConfigureAwait(false);
        return JsonValueConversion.Deserialize<T>(value, options);
    }

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
