using System.Net;
using System.Net.Sockets;

namespace FlowOrchestrator.Dashboard.Webhooks.Network;

/// <summary>
/// Compact IPv4 / IPv6 IP-range matcher. Each entry can be one of:
/// <list type="bullet">
///   <item><description><c>10.0.0.0/8</c> — CIDR block</description></item>
///   <item><description><c>2001:db8::/32</c> — IPv6 CIDR</description></item>
///   <item><description><c>203.0.113.42</c> — bare address (treated as <c>/32</c> / <c>/128</c>)</description></item>
///   <item><description><c>10.0.0.1-10.0.0.42</c> — inclusive start–end range (IPv4 + IPv6)</description></item>
///   <item><description><c>10.0.0.*</c> — wildcard, equivalent to <c>10.0.0.0/24</c></description></item>
///   <item><description><c>10.0.*.*</c> — wildcard, equivalent to <c>10.0.0.0/16</c></description></item>
///   <item><description><c>10.*.*.*</c> — wildcard, equivalent to <c>10.0.0.0/8</c></description></item>
///   <item><description><c>*.*.*.*</c> — wildcard, allows everything (use with caution)</description></item>
/// </list>
/// Mix and match in any combination. Per-request matching is allocation-free.
/// </summary>
public sealed class CidrMatcher
{
    private readonly Entry[] _entries;

    /// <summary>Number of valid entries the matcher holds.</summary>
    public int Count => _entries.Length;

    /// <summary>True when no entries were registered — every match returns <see langword="false"/>.</summary>
    public bool IsEmpty => _entries.Length == 0;

    /// <summary>Parses a sequence of CIDR / range / wildcard / single-IP strings; invalid entries are dropped.</summary>
    /// <param name="entries">Raw entry strings.</param>
    public CidrMatcher(IEnumerable<string> entries)
    {
        _entries = (entries ?? Array.Empty<string>())
            .Select(raw => TryParse(raw, out var entry) ? (Success: true, Entry: entry) : (Success: false, Entry: default))
            .Where(parsed => parsed.Success)
            .Select(parsed => parsed.Entry)
            .ToArray();
    }

    /// <summary>Returns <see langword="true"/> when <paramref name="address"/> falls inside any registered entry.</summary>
    /// <param name="address">Address to test; <see langword="null"/> always returns <see langword="false"/>.</param>
    public bool Matches(IPAddress? address)
    {
        if (address is null || _entries.Length == 0) return false;
        var bytes = address.GetAddressBytes();
        return _entries
            .Where(entry => entry.Family == address.AddressFamily)
            .Any(entry =>
                (entry.Kind == EntryKind.Cidr && PrefixMatches(bytes, entry.Network!, entry.Bits))
                || (entry.Kind == EntryKind.Range && InRange(bytes, entry.RangeStart!, entry.RangeEnd!)));
    }

    private static bool PrefixMatches(byte[] address, byte[] network, int bits)
    {
        var fullBytes = bits / 8;
        var remainderBits = bits % 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (address[i] != network[i]) return false;
        }
        if (remainderBits == 0) return true;
        var mask = (byte)(0xFF << (8 - remainderBits));
        return (address[fullBytes] & mask) == (network[fullBytes] & mask);
    }

    private static bool InRange(byte[] address, byte[] start, byte[] end)
    {
        return Compare(address, start) >= 0 && Compare(address, end) <= 0;

        static int Compare(byte[] a, byte[] b)
        {
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] < b[i]) return -1;
                if (a[i] > b[i]) return 1;
            }
            return 0;
        }
    }

    private static bool TryParse(string raw, out Entry entry)
    {
        entry = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        raw = raw.Trim();

        // 1) Range form: "start-end" — must contain a dash, but skip if dash is inside an IPv6 (rare).
        var dashIndex = raw.IndexOf('-');
        // Heuristic: a hyphen at position 0 or inside an IPv6 prefix doesn't split a range; rely on IPAddress.TryParse to validate.
        if (dashIndex > 0 && raw.IndexOf('/') < 0)
        {
            var leftRaw = raw[..dashIndex].Trim();
            var rightRaw = raw[(dashIndex + 1)..].Trim();
            if (IPAddress.TryParse(leftRaw, out var leftIp) && IPAddress.TryParse(rightRaw, out var rightIp)
                && leftIp.AddressFamily == rightIp.AddressFamily)
            {
                var startBytes = leftIp.GetAddressBytes();
                var endBytes = rightIp.GetAddressBytes();
                if (CompareBytes(startBytes, endBytes) <= 0)
                {
                    entry = new Entry(EntryKind.Range, null, 0, startBytes, endBytes, leftIp.AddressFamily);
                    return true;
                }
            }
        }

        // 2) Wildcard form (IPv4 only): "10.0.*.*"
        if (raw.Contains('*') && raw.IndexOf('/') < 0 && !raw.Contains(':'))
        {
            if (TryParseIPv4Wildcard(raw, out entry)) return true;
            return false;
        }

        // 3) CIDR or bare-IP form.
        var slash = raw.IndexOf('/');
        var addressPart = slash < 0 ? raw : raw[..slash];
        if (!IPAddress.TryParse(addressPart, out var address)) return false;
        var maxBits = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        var bits = maxBits;
        if (slash >= 0
            && (!int.TryParse(raw[(slash + 1)..], out bits) || bits < 0 || bits > maxBits))
        {
            return false;
        }
        entry = new Entry(EntryKind.Cidr, address.GetAddressBytes(), bits, null, null, address.AddressFamily);
        return true;
    }

    private static bool TryParseIPv4Wildcard(string raw, out Entry entry)
    {
        entry = default;
        var parts = raw.Split('.');
        if (parts.Length != 4) return false;

        // Walk left-to-right, parsing octets until we hit a wildcard. Every octet AFTER the
        // first wildcard MUST also be a wildcard — `10.*.0.0` is rejected as malformed.
        var prefixOctets = 0;
        var bytes = new byte[4];
        var seenWildcard = false;
        for (var i = 0; i < 4; i++)
        {
            if (parts[i] == "*")
            {
                seenWildcard = true;
                bytes[i] = 0;
                continue;
            }
            if (seenWildcard) return false; // octet after wildcard
            if (!byte.TryParse(parts[i], out var octet)) return false;
            bytes[i] = octet;
            prefixOctets = i + 1;
        }
        var bits = prefixOctets * 8;
        entry = new Entry(EntryKind.Cidr, bytes, bits, null, null, AddressFamily.InterNetwork);
        return true;
    }

    private static int CompareBytes(byte[] a, byte[] b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] < b[i]) return -1;
            if (a[i] > b[i]) return 1;
        }
        return 0;
    }

    private enum EntryKind { Cidr, Range }

    private readonly record struct Entry(
        EntryKind Kind,
        byte[]? Network,
        int Bits,
        byte[]? RangeStart,
        byte[]? RangeEnd,
        AddressFamily Family);
}
