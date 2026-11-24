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
/// <remarks>
/// The existing <c>WebhookClientIpResolutionTests</c> model a chain "client, proxy1, proxy2" arriving from
/// proxy2, i.e. a proxy listing itself, which no proxy does. Under that model the code picks the entry one
/// further left than the trusted one, which is attacker-written. These tests describe real deployments.
/// </remarks>
public sealed class ForwardedForProxySemanticsTests
{
    private static HttpContext Context(string xff, string remote)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Forwarded-For"] = xff;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        return ctx;
    }

    [Fact]
    public void Single_nginx_proxy_resolves_the_client_it_appended()
    {
        // Arrange — client 203.0.113.50 → nginx (10.0.0.1, $proxy_add_x_forwarded_for) → app.
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
}
