using System.Globalization;

namespace FlowOrchestrator.Dashboard;

/// <summary>
/// Reads an <c>Accept-Encoding</c> request header without allocating: runs on every dashboard response,
/// including the 5-second auto-refresh polls.
/// </summary>
internal static class AcceptEncoding
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="acceptEncoding"/> advertises <paramref name="token"/>
    /// as a coding with a q-value greater than zero.
    /// </summary>
    /// <param name="acceptEncoding">The raw header value; <see langword="null"/> or empty accepts nothing.</param>
    /// <param name="token">The coding to look for, e.g. <c>br</c> or <c>gzip</c>; compared case-insensitively.</param>
    /// <returns><see langword="true"/> when the coding is listed and not disabled with <c>q=0</c>.</returns>
    /// <remarks>
    /// Per RFC 9110 §12.5.3, an entry like <c>br;q=0</c> means "do not use Brotli even if you support it": an
    /// explicit zero q-value excludes the coding, any other (or unparseable, or omitted) q-value accepts it.
    /// Precedence between codings (preferring <c>br;q=0.9</c> over <c>gzip;q=0.5</c>) is not implemented —
    /// real-world browsers emit unweighted lists or the <c>q=0</c> disable form. Parsed over spans: no
    /// <c>Split</c>, no LINQ, no substrings.
    /// </remarks>
    public static bool Has(string? acceptEncoding, string token)
    {
        if (string.IsNullOrEmpty(acceptEncoding))
        {
            return false;
        }

        var header = acceptEncoding.AsSpan();
        foreach (var entryRange in header.Split(','))
        {
            var entry = header[entryRange].Trim();
            if (entry.IsEmpty)
            {
                continue;
            }

            // Split "<coding>" from "<coding>;<params>".
            var semi = entry.IndexOf(';');
            var coding = (semi < 0 ? entry : entry[..semi]).Trim();
            if (!coding.Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (semi < 0)
            {
                return true; // bare coding — implicitly q=1
            }

            // Walk the ";q=<value>" parameter list: the first q parameter decides.
            var parameters = entry[(semi + 1)..];
            foreach (var paramRange in parameters.Split(';'))
            {
                var parameter = parameters[paramRange].Trim();
                if (!parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return !(double.TryParse(parameter[2..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var q)
                         && q <= 0d);
            }

            return true; // matched coding, no q parameter
        }

        return false;
    }
}
