using System.Net;
using FlowOrchestrator.Dashboard.Webhooks.Network;

namespace FlowOrchestrator.Dashboard.UnitTests.Network;

/// <summary>Tests for the CIDR matcher (IPv4 + IPv6, edge cases).</summary>
public sealed class CidrMatcherTests
{
    [Theory]
    [InlineData("10.0.0.0/8", "10.5.6.7", true)]
    [InlineData("10.0.0.0/8", "11.0.0.0", false)]
    [InlineData("192.168.1.0/24", "192.168.1.42", true)]
    [InlineData("192.168.1.0/24", "192.168.2.0", false)]
    [InlineData("203.0.113.42", "203.0.113.42", true)]
    [InlineData("203.0.113.42", "203.0.113.41", false)]
    public void Matches_ipv4(string cidr, string address, bool expected)
    {
        // Arrange
        var matcher = new CidrMatcher(new[] { cidr });

        // Act
        var result = matcher.Matches(IPAddress.Parse(address));

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("2001:db8::/32", "2001:db8:1234::1", true)]
    [InlineData("2001:db8::/32", "2002::1", false)]
    public void Matches_ipv6(string cidr, string address, bool expected)
    {
        // Arrange
        var matcher = new CidrMatcher(new[] { cidr });

        // Act
        var result = matcher.Matches(IPAddress.Parse(address));

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Empty_input_returns_false()
    {
        // Arrange
        var matcher = new CidrMatcher(Array.Empty<string>());

        // Act
        var result = matcher.Matches(IPAddress.Parse("10.0.0.1"));

        // Assert
        Assert.False(result);
        Assert.True(matcher.IsEmpty);
    }

    [Fact]
    public void Invalid_entries_are_skipped()
    {
        // Arrange
        var matcher = new CidrMatcher(new[] { "garbage", "10.0.0.0/8", "10.0.0.0/99" });

        // Act
        var hit = matcher.Matches(IPAddress.Parse("10.1.2.3"));
        var miss = matcher.Matches(IPAddress.Parse("11.1.2.3"));

        // Assert
        Assert.True(hit);
        Assert.False(miss);
        Assert.Equal(1, matcher.Count);
    }

    [Fact]
    public void Known_publishers_resolve_lists()
    {
        // Arrange + Act
        var github = KnownPublisherCidrs.TryGet("GitHub");
        var stripe = KnownPublisherCidrs.TryGet("stripe");
        var unknown = KnownPublisherCidrs.TryGet("nope");

        // Assert
        Assert.NotNull(github);
        Assert.NotEmpty(github!);
        Assert.NotNull(stripe);
        Assert.NotEmpty(stripe!);
        Assert.Null(unknown);
    }

    // ── Range syntax (start-end) ──────────────────────────────────────────────

    [Theory]
    [InlineData("10.0.0.10-10.0.0.20", "10.0.0.10", true)]
    [InlineData("10.0.0.10-10.0.0.20", "10.0.0.15", true)]
    [InlineData("10.0.0.10-10.0.0.20", "10.0.0.20", true)]
    [InlineData("10.0.0.10-10.0.0.20", "10.0.0.9", false)]
    [InlineData("10.0.0.10-10.0.0.20", "10.0.0.21", false)]
    [InlineData("10.0.0.10-10.0.0.20", "10.0.1.10", false)]
    public void Matches_ipv4_range(string range, string address, bool expected)
    {
        var matcher = new CidrMatcher(new[] { range });
        Assert.Equal(expected, matcher.Matches(IPAddress.Parse(address)));
    }

    [Fact]
    public void Matches_ipv6_range()
    {
        // Arrange
        var matcher = new CidrMatcher(new[] { "2001:db8::1-2001:db8::ff" });

        // Act + Assert
        Assert.True(matcher.Matches(IPAddress.Parse("2001:db8::42")));
        Assert.False(matcher.Matches(IPAddress.Parse("2001:db8::1:0")));
    }

    [Fact]
    public void Reversed_range_is_rejected()
    {
        // Arrange — start > end is malformed.
        var matcher = new CidrMatcher(new[] { "10.0.0.99-10.0.0.10" });

        // Act + Assert — entry dropped.
        Assert.True(matcher.IsEmpty);
    }

    // ── Wildcard syntax (10.0.*.*) ───────────────────────────────────────────

    [Theory]
    [InlineData("10.0.0.*", "10.0.0.5", true)]
    [InlineData("10.0.0.*", "10.0.1.5", false)]
    [InlineData("10.0.*.*", "10.0.5.5", true)]
    [InlineData("10.0.*.*", "10.1.0.0", false)]
    [InlineData("10.*.*.*", "10.255.255.255", true)]
    [InlineData("10.*.*.*", "11.0.0.0", false)]
    public void Matches_ipv4_wildcard(string pattern, string address, bool expected)
    {
        var matcher = new CidrMatcher(new[] { pattern });
        Assert.Equal(expected, matcher.Matches(IPAddress.Parse(address)));
    }

    [Fact]
    public void Wildcard_with_octet_after_star_is_rejected()
    {
        var matcher = new CidrMatcher(new[] { "10.*.0.0" });
        Assert.True(matcher.IsEmpty);
    }

    // ── Mix-and-match ─────────────────────────────────────────────────────────

    [Fact]
    public void Mixed_cidr_range_and_wildcard_in_same_matcher()
    {
        // Arrange
        var matcher = new CidrMatcher(new[]
        {
            "10.0.0.0/8",                       // CIDR
            "172.16.0.10-172.16.0.20",          // range
            "192.168.5.*",                      // wildcard
            "203.0.113.42",                     // bare IP
        });

        // Act + Assert
        Assert.True(matcher.Matches(IPAddress.Parse("10.5.5.5")));      // CIDR
        Assert.True(matcher.Matches(IPAddress.Parse("172.16.0.15")));   // range
        Assert.True(matcher.Matches(IPAddress.Parse("192.168.5.99")));  // wildcard
        Assert.True(matcher.Matches(IPAddress.Parse("203.0.113.42")));  // bare
        Assert.False(matcher.Matches(IPAddress.Parse("203.0.113.43"))); // outside bare
        Assert.False(matcher.Matches(IPAddress.Parse("172.16.0.21"))); // outside range
    }

    // ── New presets ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("github")]
    [InlineData("stripe")]
    [InlineData("shopify")]
    [InlineData("twilio")]
    [InlineData("square")]
    [InlineData("atlassian")]
    [InlineData("bitbucket")]
    [InlineData("slack")]
    [InlineData("mailgun")]
    [InlineData("zoom")]
    [InlineData("local")]
    [InlineData("private")]
    [InlineData("localhost")]
    public void Every_known_preset_resolves_to_non_empty_list(string name)
    {
        var cidrs = KnownPublisherCidrs.TryGet(name);
        Assert.NotNull(cidrs);
        Assert.NotEmpty(cidrs!);
        var matcher = new CidrMatcher(cidrs!);
        Assert.False(matcher.IsEmpty);
    }

    [Fact]
    public void Local_preset_matches_loopback_and_rfc1918()
    {
        var matcher = new CidrMatcher(KnownPublisherCidrs.LocalNetworks);
        Assert.True(matcher.Matches(IPAddress.Parse("127.0.0.1")));
        Assert.True(matcher.Matches(IPAddress.Parse("::1")));
        Assert.True(matcher.Matches(IPAddress.Parse("10.5.5.5")));
        Assert.True(matcher.Matches(IPAddress.Parse("192.168.1.1")));
        Assert.True(matcher.Matches(IPAddress.Parse("172.20.0.1")));
        Assert.False(matcher.Matches(IPAddress.Parse("8.8.8.8")));
    }
}
