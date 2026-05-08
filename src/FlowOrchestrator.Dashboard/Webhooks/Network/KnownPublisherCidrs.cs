namespace FlowOrchestrator.Dashboard.Webhooks.Network;

/// <summary>
/// Hard-coded snapshots of well-known webhook publisher IP ranges.
/// Operators reference these via the <c>webhookIpAllowListPreset</c> manifest
/// field instead of pasting CIDR lists into every flow definition.
/// </summary>
/// <remarks>
/// These ranges drift over time. Treat the values as a defensive default; the
/// authoritative source for each publisher should be checked when locking down
/// a production endpoint. Keys are matched case-insensitively.
/// </remarks>
public static class KnownPublisherCidrs
{
    /// <summary>
    /// IPv4 / IPv6 CIDR ranges GitHub uses for webhook deliveries.
    /// Source: <c>https://api.github.com/meta</c> (key <c>"hooks"</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> GitHub = new[]
    {
        "192.30.252.0/22",
        "185.199.108.0/22",
        "140.82.112.0/20",
        "143.55.64.0/20",
        "2a0a:a440::/29",
        "2606:50c0::/32",
    };

    /// <summary>Stripe's published webhook source range. Source: Stripe webhook docs.</summary>
    public static readonly IReadOnlyList<string> Stripe = new[]
    {
        "3.18.12.63/32",
        "3.130.192.231/32",
        "13.235.14.237/32",
        "13.235.122.149/32",
        "18.211.135.69/32",
        "35.154.171.200/32",
        "52.15.183.38/32",
        "54.88.130.119/32",
        "54.88.130.237/32",
        "54.187.174.169/32",
        "54.187.205.235/32",
        "54.187.216.72/32",
    };

    /// <summary>
    /// Returns the registered preset (case-insensitive) or
    /// <see langword="null"/> when the name is unknown.
    /// </summary>
    /// <param name="name">Preset name (e.g. <c>"github"</c>, <c>"stripe"</c>).</param>
    public static IReadOnlyList<string>? TryGet(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name.ToLowerInvariant() switch
        {
            "github" => GitHub,
            "stripe" => Stripe,
            _ => null,
        };
    }
}
