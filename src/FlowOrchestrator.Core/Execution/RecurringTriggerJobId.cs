namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Builds and parses the stable recurring-job identifier shared by every runtime's cron scheduler,
/// the schedule-state store and the dashboard: <c>flow-{flowId:D}-{triggerKey}</c>.
/// </summary>
/// <remarks>
/// The format is persisted — it keys Hangfire recurring jobs, rows in the schedule-state tables and
/// Service Bus cron messages — so it must never change shape.
/// </remarks>
public static class RecurringTriggerJobId
{
    /// <summary>The literal prefix every recurring-trigger job id starts with.</summary>
    public const string Prefix = "flow-";

    // "flow-" + 36-char "D" GUID + "-" separator.
    private const int GuidLength = 36;
    private const int TriggerKeyOffset = 5 + GuidLength + 1;

    /// <summary>Builds the job id for the cron trigger <paramref name="triggerKey"/> of flow <paramref name="flowId"/>.</summary>
    /// <param name="flowId">The flow that owns the trigger.</param>
    /// <param name="triggerKey">The trigger's key in the flow manifest.</param>
    /// <returns>The identifier, e.g. <c>flow-0b1c…-schedule</c>.</returns>
    public static string Create(Guid flowId, string triggerKey)
    {
        ArgumentNullException.ThrowIfNull(triggerKey);
        return string.Create(
            TriggerKeyOffset + triggerKey.Length,
            (flowId, triggerKey),
            static (span, state) =>
            {
                Prefix.AsSpan().CopyTo(span);
                state.flowId.TryFormat(span[Prefix.Length..], out _, "D");
                span[Prefix.Length + GuidLength] = '-';
                state.triggerKey.AsSpan().CopyTo(span[TriggerKeyOffset..]);
            });
    }

    /// <summary>
    /// Parses a job id produced by <see cref="Create"/> back into its flow id and trigger key.
    /// </summary>
    /// <param name="jobId">The candidate job id.</param>
    /// <param name="flowId">The parsed flow id, or <see cref="Guid.Empty"/> on failure.</param>
    /// <param name="triggerKey">The parsed trigger key, or an empty string on failure.</param>
    /// <returns><see langword="true"/> when <paramref name="jobId"/> has the expected shape and a non-empty trigger key.</returns>
    public static bool TryParse(string? jobId, out Guid flowId, out string triggerKey)
    {
        flowId = Guid.Empty;
        triggerKey = string.Empty;

        if (jobId is null
            || jobId.Length <= TriggerKeyOffset
            || !jobId.StartsWith(Prefix, StringComparison.Ordinal)
            || jobId[Prefix.Length + GuidLength] != '-'
            || !Guid.TryParseExact(jobId.AsSpan(Prefix.Length, GuidLength), "D", out var parsed))
        {
            return false;
        }

        flowId = parsed;
        triggerKey = jobId[TriggerKeyOffset..];
        return true;
    }
}
