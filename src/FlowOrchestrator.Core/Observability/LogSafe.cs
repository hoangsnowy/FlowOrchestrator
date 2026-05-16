namespace FlowOrchestrator.Core.Observability;

/// <summary>
/// Helpers that strip control characters from values flowing into structured-logging arguments,
/// blocking CWE-117 log-forging where a tainted string contains <c>\r</c>/<c>\n</c> and would
/// otherwise let an attacker forge a fake log line.
/// </summary>
/// <remarks>
/// Apply at the boundary between externally-influenced data (job IDs derived from manifest trigger
/// keys, cron expressions persisted from API overrides, step keys from manifests) and the call to
/// <c>ILogger.Log*</c>. The replacement is intentionally simple (CR/LF/NUL → <c>_</c>) so the
/// sanitized value remains human-readable while flat-text log sinks (Console, file appenders) can
/// no longer be tricked into rendering a multi-line record.
/// </remarks>
public static class LogSafe
{
    /// <summary>
    /// Returns <paramref name="value"/> with carriage-return, line-feed, and NUL characters
    /// replaced by underscores. Returns <see langword="null"/> when input is <see langword="null"/>.
    /// </summary>
    /// <param name="value">The value to sanitize.</param>
    /// <returns>A sanitized copy safe to interpolate into a single-line log record.</returns>
    public static string? Strip(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.IndexOfAny(_controls) < 0) return value;

        return value
            .Replace('\r', '_')
            .Replace('\n', '_')
            .Replace('\0', '_');
    }

    private static readonly char[] _controls = ['\r', '\n', '\0'];
}
