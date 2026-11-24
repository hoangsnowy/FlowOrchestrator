using System.Net;
using FlowOrchestrator.Dashboard.Webhooks;
using Microsoft.AspNetCore.Http;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Pins <see cref="WebhookSecurityPipeline.ResolveClientIp"/> to how reverse proxies actually build
/// <c>X-Forwarded-For</c>: each proxy APPENDS the address of the peer it received the request from, and never
/// its own. Behind N trusted proxies the app's socket peer is the last proxy and the header ends with N entries
/// written by proxies, so the client is <c>hops[^N]</c> — the same rule as ASP.NET Core's
/// <c>ForwardedHeadersMiddleware</c> with <c>ForwardLimit = N</c>.
/// </summary>
public sealed class ForwardedForProxySemanticsTests
{
    private static HttpContext Context(string? xff, string? remote)
    {
        var ctx = new DefaultHttpContext();
        if (xff is not null)
            ctx.Request.Headers["X-Forwarded-For"] = xff;
        ctx.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
        return ctx;
    }

    [Fact]
    public void Single_nginx_proxy_resolves_the_client_it_appended()
    {
        // Arrange — client 203.0.113.50 → nginx (10.0.0.1, $proxy_add_x_forwarded_for) → app; the caller sent
        // no X-Forwarded-For of its own.
        var http = Context(xff: "203.0.113.50", remote: "10.0.0.1");

        // Act
        var resolved = WebhookSecurityPipeline.ResolveClientIp(http, forwardedDepth: 1);

        // Assert — the proxy's own address would put every caller in one rate-limit bucket and fail every
        // publisher allow-list.
        Assert.Equal(IPAddress.Parse("203.0.113.50"), resolved);
    }

    [Fact]
    public void Single_proxy_ignores_an_attacker_prepended_allowlisted_address()
    {
        // Arrange — attacker 203.0.113.50 sends "X-Forwarded-For: 192.30.252.10" (a GitHub hook address);
        // nginx appends the real peer, so the app sees "192.30.252.10, 203.0.113.50".
        var http = Context(xff: "192.30.252.10, 203.0.113.50", remote: "10.0.0.1");

        // Act
        var resolved = WebhookSecurityPipeline.ResolveClientIp(http, forwardedDepth: 1);

        // Assert
        Assert.Equal(IPAddress.Parse("203.0.113.50"), resolved);
    }

    [Fact]
    public void Two_proxies_resolve_the_entry_written_by_the_outer_proxy()
    {
        // Arrange — client 203.0.113.50 → CDN 198.51.100.1 → ingress 10.0.0.1 → app.
        // CDN appends 203.0.113.50; ingress appends 198.51.100.1.
        var http = Context(xff: "203.0.113.50, 198.51.100.1", remote: "10.0.0.1");

        // Act
        var resolved = WebhookSecurityPipeline.ResolveClientIp(http, forwardedDepth: 2);

        // Assert
        Assert.Equal(IPAddress.Parse("203.0.113.50"), resolved);
    }

    [Fact]
    public void Two_proxies_ignore_entries_the_caller_forged_in_front()
    {
        // Arrange — as above, but the caller sent "X-Forwarded-For: 192.30.252.10, 140.82.112.1".
        var http = Context(xff: "192.30.252.10, 140.82.112.1, 203.0.113.50, 198.51.100.1", remote: "10.0.0.1");

        // Act
        var resolved = WebhookSecurityPipeline.ResolveClientIp(http, forwardedDepth: 2);

        // Assert
        Assert.Equal(IPAddress.Parse("203.0.113.50"), resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("203.0.113.50")]
    public void Fewer_entries_than_trusted_proxies_fall_back_to_the_socket_address(string? xff)
    {
        // Arrange — depth 2 but the request did not pass through both proxies (no header, or one entry).
        var http = Context(xff: xff, remote: "198.51.100.10");

        // Act
        var resolved = WebhookSecurityPipeline.ResolveClientIp(http, forwardedDepth: 2);

        // Assert
        Assert.Equal(IPAddress.Parse("198.51.100.10"), resolved);
    }

    [Fact]
    public void Whitespace_empty_entries_and_ipv6_are_handled()
    {
        // Arrange — irregular spacing, an empty element and an IPv6 client.
        var http = Context(xff: "  192.30.252.10 ,, 2001:db8::7  ,  ", remote: "10.0.0.1");

        // Act
        var resolved = WebhookSecurityPipeline.ResolveClientIp(http, forwardedDepth: 1);

        // Assert
        Assert.Equal(IPAddress.Parse("2001:db8::7"), resolved);
    }

    [Fact]
    public void Multiple_header_lines_are_read_as_one_list_in_arrival_order()
    {
        // Arrange — the caller's own header line comes first, the proxy's appended line last.
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Forwarded-For"] = new Microsoft.Extensions.Primitives.StringValues(["192.30.252.10", "203.0.113.50"]);
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");

        // Act
        var resolved = WebhookSecurityPipeline.ResolveClientIp(ctx, forwardedDepth: 1);

        // Assert
        Assert.Equal(IPAddress.Parse("203.0.113.50"), resolved);
    }

    [Fact]
    public void Unparseable_client_entry_falls_back_to_the_socket_address()
    {
        // Arrange — a proxy that writes "unknown" (allowed by some proxies for obfuscated peers).
        var http = Context(xff: "192.30.252.10, unknown", remote: "10.0.0.1");

        // Act
        var resolved = WebhookSecurityPipeline.ResolveClientIp(http, forwardedDepth: 1);

        // Assert — never the caller-written entry to its left.
        Assert.Equal(IPAddress.Parse("10.0.0.1"), resolved);
    }
}
