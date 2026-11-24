using System.Net;
using FlowOrchestrator.Dashboard.Webhooks;
using Microsoft.AspNetCore.Http;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Unit coverage for <see cref="WebhookSecurityPipeline.ResolveClientIp"/> X-Forwarded-For handling.
/// Proxies APPEND the address of the peer they received the request from, so behind N trusted proxies the
/// client is the entry N positions from the right; a header with fewer than N entries is not trusted at all
/// and the transport-level remote address is used (never the leftmost, caller-controlled entry).
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
    public void ResolveClientIp_HopsEqualToDepth_ReturnsTheEntryTheOuterProxyAppended()
    {
        // Arrange — boundary: hops.Length == forwardedDepth. Client → outer proxy 10.0.0.1 → inner proxy
        // 10.0.0.2 → app. Each proxy appends the peer it received the request from, so the outer proxy wrote
        // the client and the inner proxy wrote the outer proxy: every entry is proxy-written and hops[^2] is
        // the client.
        var client = IPAddress.Parse("203.0.113.99");
        var outerProxy = IPAddress.Parse("10.0.0.1");
        var http = Context(xff: $"{client}, {outerProxy}", remote: IPAddress.Parse("10.0.0.2"));

        // Act
        var resolved = WebhookSecurityPipeline.ResolveClientIp(http, forwardedDepth: 2);

        // Assert
        Assert.Equal(client, resolved);
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
    public void ResolveClientIp_HopsLongerThanDepth_ReturnsClientAndIgnoresCallerSuppliedEntries()
    {
        // Arrange — client → proxy1 → proxy2 → app with depth 2, and the client sent its own
        // "X-Forwarded-For: <forged>". proxy1 appends the client, proxy2 appends proxy1 (no proxy ever lists
        // itself; proxy2 is the socket peer), so the header reads "forged, client, proxy1". The entries left of
        // the two proxy-written ones came from the caller and must not be selected.
        var forged = IPAddress.Parse("192.30.252.10");
        var client = IPAddress.Parse("203.0.113.50");
        var proxy1 = IPAddress.Parse("10.0.0.1");
        var proxy2 = IPAddress.Parse("10.0.0.2");
        var http = Context(xff: $"{forged}, {client}, {proxy1}", remote: proxy2);

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
