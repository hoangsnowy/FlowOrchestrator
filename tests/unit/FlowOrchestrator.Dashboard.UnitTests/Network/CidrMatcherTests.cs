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
}
