using System.Net;
using FlowOrchestrator.Dashboard.Webhooks.Network;

namespace FlowOrchestrator.Dashboard.UnitTests.Network;

/// <summary>
/// End-to-end tests that walk the published-publisher preset → CIDR list →
/// matcher pipeline (the path used by <c>webhookIpAllowListPreset</c>).
/// </summary>
public sealed class KnownPublisherCidrsResolutionTests
{
    [Fact]
    public void GitHub_preset_resolves_to_non_empty_matcher()
    {
        // Arrange
        var cidrs = KnownPublisherCidrs.TryGet("github");

        // Act
        var matcher = new CidrMatcher(cidrs!);

        // Assert
        Assert.NotNull(cidrs);
        Assert.False(matcher.IsEmpty);
        // GitHub publishes 192.30.252.0/22 — sample IP from inside that range.
        Assert.True(matcher.Matches(IPAddress.Parse("192.30.253.10")));
    }

    [Fact]
    public void Stripe_preset_matches_a_known_published_address()
    {
        // Arrange
        var cidrs = KnownPublisherCidrs.TryGet("stripe");

        // Act
        var matcher = new CidrMatcher(cidrs!);

        // Assert
        Assert.NotNull(cidrs);
        Assert.True(matcher.Matches(IPAddress.Parse("3.18.12.63")));
    }

    [Fact]
    public void Unknown_preset_returns_null()
    {
        Assert.Null(KnownPublisherCidrs.TryGet("nonsense"));
    }

    [Fact]
    public void Preset_lookup_is_case_insensitive()
    {
        Assert.NotNull(KnownPublisherCidrs.TryGet("GitHub"));
        Assert.NotNull(KnownPublisherCidrs.TryGet("STRIPE"));
    }
}
