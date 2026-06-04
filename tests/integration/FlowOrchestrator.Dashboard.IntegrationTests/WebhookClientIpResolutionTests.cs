using System.Net;
using FlowOrchestrator.Dashboard.Webhooks;
using Microsoft.AspNetCore.Http;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Unit coverage for <see cref="WebhookSecurityPipeline.ResolveClientIp"/> X-Forwarded-For handling.
/// Focus: the spoofing regression where a header carrying fewer hops than the configured trusted-proxy
/// depth caused the index to clamp to 0 — the leftmost, fully attacker-controlled XFF entry — which was
/// then trusted for the IP allowlist and the rate-limit key. The fix must NOT return that leftmost value;
/// it falls back to the transport-level remote address instead.
/// </summary>
public sealed class WebhookClientIpResolutionTests
{
    private static HttpContext Context(string? xff, IPAddress? remote)
    {
        var ctx = new DefaultHttpContext();
        if (xff is not null)
        {
            ctx.Request.Headers["X-Forwarded-For"] = xff;
        }
        ctx.Connection.RemoteIpAddress = remote;
        return ctx;
    }

    [Fact]
    public void ResolveClientIp_HopsShorterThanDepth_DoesNotReturnLeftmostAttackerValue()
    {
        // Arrange — depth says "2 trusted proxies" but the header has only ONE hop (the attacker's
        // spoofed value). The buggy clamp would have returned this leftmost entry.
        var attacker = IPAddress.Parse("203.0.113.7");
        var realRemote = IPAddress.Parse("198.51.100.10");
        var http = Context(xff: attacker.ToString(), remote: realRemote);

        // Act
        var resolved = WebhookSecurityPipeline.ResolveClientIp(http, forwardedDepth: 2);

        // Assert — the spoofed leftmost value is NOT trusted; we fall back to the socket address.
        Assert.NotEqual(attacker, resolved);
        Assert.Equal(realRemote, resolved);
    }

    [Fact]
    public void ResolveClientIp_HopsEqualToDepth_DoesNotReturnLeftmostAttackerValue()
    {
        // Arrange — boundary: hops.Length == forwardedDepth. There is no hop "before" the trusted
        // proxies, so the entire chain is untrusted and must be ignored.
        var attacker = IPAddress.Parse("203.0.113.99");
        var proxy = IPAddress.Parse("10.0.0.1");
        var realRemote = IPAddress.Parse("198.51.100.10");
        var http = Context(xff: $"{attacker}, {proxy}", remote: realRemote);

        // Act
        var resolved = WebhookSecurityPipeline.ResolveClientIp(http, forwardedDepth: 2);

        // Assert
        Assert.NotEqual(attacker, resolved);
        Assert.Equal(realRemote, resolved);
    }

    [Fact]
    public void ResolveClientIp_HopsShorterThanDepth_FallsBackToNullWhenNoRemote()
    {
        // Arrange — no transport remote address available; the allowlist fails safe on null, so the
        // method must surface null rather than the attacker-supplied XFF value.
        var attacker = IPAddress.Parse("203.0.113.7");
        var http = Context(xff: attacker.ToString(), remote: null);

        // Act
        var resolved = WebhookSecurityPipeline.ResolveClientIp(http, forwardedDepth: 3);

        // Assert
        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveClientIp_HopsLongerThanDepth_ReturnsClientHopBeforeTrustedProxies()
    {
        // Arrange — legitimate case: client, proxy1, proxy2 with depth 2. The entry just before the
        // two trusted proxies (the real client) is selected.
        var client = IPAddress.Parse("203.0.113.50");
        var proxy1 = IPAddress.Parse("10.0.0.1");
        var proxy2 = IPAddress.Parse("10.0.0.2");
        var http = Context(xff: $"{client}, {proxy1}, {proxy2}", remote: IPAddress.Parse("10.0.0.2"));

        // Act
        var resolved = WebhookSecurityPipeline.ResolveClientIp(http, forwardedDepth: 2);

        // Assert
        Assert.Equal(client, resolved);
    }

    [Fact]
    public void ResolveClientIp_ZeroDepth_IgnoresHeaderAndUsesRemote()
    {
        // Arrange — depth 0 disables XFF entirely; the socket address is authoritative.
        var attacker = IPAddress.Parse("203.0.113.7");
        var realRemote = IPAddress.Parse("198.51.100.10");
        var http = Context(xff: attacker.ToString(), remote: realRemote);

        // Act
        var resolved = WebhookSecurityPipeline.ResolveClientIp(http, forwardedDepth: 0);

        // Assert
        Assert.Equal(realRemote, resolved);
    }
}
