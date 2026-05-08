using System.Net;
using System.Net.Sockets;

namespace FlowOrchestrator.Dashboard.Webhooks.Network;

/// <summary>
/// Compact IPv4 / IPv6 CIDR matcher. Accepts entries like <c>"10.0.0.0/8"</c>,
/// <c>"2001:db8::/32"</c>, or a bare address (treated as /32 / /128).
/// Constructed once at startup; per-request matching is allocation-free.
/// </summary>
public sealed class CidrMatcher
{
    private readonly Entry[] _entries;

    /// <summary>Number of CIDR entries actively held.</summary>
    public int Count => _entries.Length;

    /// <summary>True when no entries were supplied — every match returns <see langword="false"/>.</summary>
    public bool IsEmpty => _entries.Length == 0;

    /// <summary>Parses a sequence of CIDR strings; invalid entries are dropped.</summary>
    /// <param name="entries">Raw CIDR / IP strings.</param>
    public CidrMatcher(IEnumerable<string> entries)
    {
        var list = new List<Entry>();
        foreach (var raw in entries ?? Array.Empty<string>())
        {
            if (TryParse(raw, out var entry)) list.Add(entry);
        }
        _entries = list.ToArray();
    }

    /// <summary>Returns <see langword="true"/> when <paramref name="address"/> falls inside any registered range.</summary>
    /// <param name="address">Address to test; <see langword="null"/> always returns <see langword="false"/>.</param>
    public bool Matches(IPAddress? address)
    {
        if (address is null || _entries.Length == 0) return false;
        var bytes = address.GetAddressBytes();
        foreach (var entry in _entries)
        {
            if (entry.Family != address.AddressFamily) continue;
            if (PrefixMatches(bytes, entry.Network, entry.Bits)) return true;
        }
        return false;
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

    private static bool TryParse(string raw, out Entry entry)
    {
        entry = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        raw = raw.Trim();
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
        entry = new Entry(address.GetAddressBytes(), bits, address.AddressFamily);
        return true;
    }

    private readonly record struct Entry(byte[] Network, int Bits, AddressFamily Family);
}
