namespace FlowOrchestrator.Dashboard.Webhooks.Network;

/// <summary>
/// Hard-coded snapshots of well-known webhook publisher IP ranges.
/// Operators reference these via the <c>webhookIpAllowListPreset</c> manifest
/// field instead of pasting CIDR lists into every flow definition.
/// </summary>
/// <remarks>
/// These ranges drift over time. Treat the values as a defensive default; the
/// authoritative source for each publisher should be checked when locking down
/// a production endpoint. Keys are matched case-insensitively. Multiple
/// presets can be combined inside a flow manifest by using
/// <c>webhookIpAllowListPresets</c> (plural — comma-separated or array).
/// </remarks>
public static class KnownPublisherCidrs
{
    /// <summary>GitHub published webhook source ranges (api.github.com/meta — <c>"hooks"</c>).</summary>
    public static readonly IReadOnlyList<string> GitHub = new[]
    {
        "192.30.252.0/22",
        "185.199.108.0/22",
        "140.82.112.0/20",
        "143.55.64.0/20",
        "2a0a:a440::/29",
        "2606:50c0::/32",
    };

    /// <summary>Stripe published webhook source range. Source: Stripe webhook docs.</summary>
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

    /// <summary>Shopify webhook source range. Source: Shopify partner help center.</summary>
    public static readonly IReadOnlyList<string> Shopify = new[]
    {
        "23.227.38.0/24",
        "104.16.0.0/12",
    };

    /// <summary>Twilio webhook source range. Source: Twilio "Network Connectivity" docs.</summary>
    public static readonly IReadOnlyList<string> Twilio = new[]
    {
        "54.172.60.0/23",
        "54.244.51.0/24",
        "177.71.206.192/26",
        "13.244.0.0/15",
        "52.215.127.0/24",
        "35.156.191.128/25",
    };

    /// <summary>Square webhook source IPs. Source: Square developer docs.</summary>
    public static readonly IReadOnlyList<string> Square = new[]
    {
        "23.21.0.0/16",
        "54.243.0.0/16",
        "107.20.0.0/16",
    };

    /// <summary>Atlassian Cloud webhook source range. Source: ip-ranges.atlassian.com.</summary>
    public static readonly IReadOnlyList<string> Atlassian = new[]
    {
        "13.52.5.0/25",
        "13.236.8.224/28",
        "18.184.99.224/28",
        "18.234.32.224/28",
        "52.215.192.128/25",
        "104.192.136.0/21",
    };

    /// <summary>Bitbucket Cloud webhook source range. Same data as <see cref="Atlassian"/>.</summary>
    public static readonly IReadOnlyList<string> Bitbucket = Atlassian;

    /// <summary>Slack webhook source range. Source: Slack API "Slack ranges" article.</summary>
    public static readonly IReadOnlyList<string> Slack = new[]
    {
        "54.241.0.0/22",
        "34.192.0.0/12",
    };

    /// <summary>Mailgun webhook source range. Source: Mailgun control panel docs.</summary>
    public static readonly IReadOnlyList<string> Mailgun = new[]
    {
        "3.19.228.0/22",
        "34.198.203.127/32",
        "34.198.178.64/32",
        "34.198.122.37/32",
    };

    /// <summary>Zoom webhook source range. Source: Zoom marketplace publisher docs.</summary>
    public static readonly IReadOnlyList<string> Zoom = new[]
    {
        "3.7.35.0/25",
        "3.21.137.128/25",
        "3.22.11.0/24",
        "3.23.93.0/24",
        "3.25.41.128/25",
        "3.80.20.128/25",
        "18.254.23.128/25",
        "44.234.52.192/26",
        "50.239.202.0/23",
        "52.61.100.128/25",
        "64.211.144.0/24",
        "64.224.32.0/19",
        "103.122.166.0/23",
        "111.33.115.0/25",
        "111.33.181.0/25",
        "147.124.96.0/19",
        "149.137.0.0/17",
        "152.105.0.0/16",
        "156.45.0.0/17",
        "159.124.0.0/16",
        "161.199.136.0/22",
        "162.12.232.0/22",
        "165.254.88.0/23",
        "166.108.64.0/18",
        "168.138.16.0/21",
        "168.138.48.0/20",
        "168.138.72.0/21",
        "170.114.0.0/16",
        "173.231.80.0/20",
        "204.80.104.0/21",
        "206.247.0.0/16",
        "207.226.132.0/24",
    };

    /// <summary>
    /// Convenient "private + loopback" preset for development environments.
    /// Allows requests from localhost (IPv4 + IPv6), RFC 1918 private ranges,
    /// and link-local ranges.
    /// </summary>
    public static readonly IReadOnlyList<string> LocalNetworks = new[]
    {
        "127.0.0.0/8",
        "::1/128",
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "fc00::/7",
        "fe80::/10",
    };

    /// <summary>
    /// Returns the registered preset (case-insensitive) or
    /// <see langword="null"/> when the name is unknown.
    /// </summary>
    /// <param name="name">Preset name (e.g. <c>"github"</c>, <c>"stripe"</c>, <c>"local"</c>).</param>
    public static IReadOnlyList<string>? TryGet(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name.ToLowerInvariant() switch
        {
            "github" => GitHub,
            "stripe" => Stripe,
            "shopify" => Shopify,
            "twilio" => Twilio,
            "square" => Square,
            "atlassian" => Atlassian,
            "bitbucket" => Bitbucket,
            "slack" => Slack,
            "mailgun" => Mailgun,
            "zoom" => Zoom,
            "local" or "localhost" or "private" => LocalNetworks,
            _ => null,
        };
    }

    /// <summary>Names of every preset known to <see cref="TryGet"/>.</summary>
    public static IReadOnlyList<string> All => new[]
    {
        "github", "stripe", "shopify", "twilio", "square",
        "atlassian", "bitbucket", "slack", "mailgun", "zoom",
        "local",
    };
}
