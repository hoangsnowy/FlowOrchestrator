using Cronos;

namespace FlowOrchestrator.Dashboard.Schedules;

/// <summary>
/// Validates a dashboard-submitted cron expression before it is persisted as a schedule override.
/// </summary>
/// <remarks>
/// Uses Cronos with the same field-count detection as every built-in runtime (5 fields = minute precision,
/// 6 fields = second precision), so an expression accepted here is one the in-memory timer, the Service Bus
/// hub and Hangfire (which also parses with Cronos) can schedule. Without this check the endpoint persisted
/// any non-blank string; the in-memory runtime then stopped every cron job in the process, and the persisted
/// value was re-applied on every restart.
/// </remarks>
internal static class CronExpressionValidator
{
    /// <summary>Returns whether <paramref name="cronExpression"/> parses as a 5- or 6-field cron expression.</summary>
    /// <param name="cronExpression">The expression submitted by the dashboard user.</param>
    /// <returns><see langword="true"/> when the expression parses; otherwise <see langword="false"/>.</returns>
    public static bool IsValid(string? cronExpression)
    {
        if (string.IsNullOrWhiteSpace(cronExpression))
        {
            return false;
        }

        var trimmed = cronExpression.AsSpan().Trim();
        var format = CountFields(trimmed) >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
        try
        {
            CronExpression.Parse(trimmed.ToString(), format);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }

    private static int CountFields(ReadOnlySpan<char> value)
    {
        var count = 0;
        var inField = false;
        foreach (var c in value)
        {
            if (c == ' ')
            {
                inField = false;
            }
            else if (!inField)
            {
                inField = true;
                count++;
            }
        }

        return count;
    }
}
