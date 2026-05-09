using System.Net;
using System.Text.Json;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Integration tests for the v1.25 dashboard webhook surface — recent-deliveries
/// listing and 24h reason histogram.
/// </summary>
public sealed class WebhookDashboardEndpointTests
{
    [Fact]
    public async Task Get_recent_returns_persisted_entries_in_descending_order()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var store = server.Services.GetRequiredService<IWebhookRejectionStore>();
        var flowId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        await store.WriteAsync(new WebhookRejectionRecord
        {
            FlowId = flowId,
            TriggerKey = "webhook",
            Reason = "signature_invalid",
            ReceivedAt = DateTimeOffset.UtcNow,
            StatusCode = 401,
            BodyBytes = 12,
        });
        await store.WriteAsync(new WebhookRejectionRecord
        {
            FlowId = flowId,
            TriggerKey = "webhook",
            Reason = "rate_limited",
            ReceivedAt = DateTimeOffset.UtcNow,
            StatusCode = 429,
            BodyBytes = 12,
        });

        // Act
        var response = await server.Client.GetAsync("/flows/api/webhooks/recent");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal("rate_limited", doc.RootElement[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Get_recent_filters_by_flow_id()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var store = server.Services.GetRequiredService<IWebhookRejectionStore>();
        var flowA = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var flowB = Guid.Parse("33333333-3333-3333-3333-333333333333");
        await store.WriteAsync(new WebhookRejectionRecord { FlowId = flowA, Reason = "rA", ReceivedAt = DateTimeOffset.UtcNow });
        await store.WriteAsync(new WebhookRejectionRecord { FlowId = flowB, Reason = "rB", ReceivedAt = DateTimeOffset.UtcNow });

        // Act
        var response = await server.Client.GetAsync($"/flows/api/webhooks/recent?flowId={flowA}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("rA", doc.RootElement[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Get_recent_with_search_filters_by_reason_trigger_or_ip()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var store = server.Services.GetRequiredService<IWebhookRejectionStore>();
        var flow = Guid.NewGuid();
        await store.WriteAsync(new WebhookRejectionRecord { FlowId = flow, TriggerKey = "github-events", Reason = "rate_limited", RemoteIp = "10.0.0.1", ReceivedAt = DateTimeOffset.UtcNow });
        await store.WriteAsync(new WebhookRejectionRecord { FlowId = flow, TriggerKey = "stripe-events", Reason = "signature_invalid", RemoteIp = "192.168.1.1", ReceivedAt = DateTimeOffset.UtcNow });

        // Act
        var response = await server.Client.GetAsync("/flows/api/webhooks/recent?q=github&includeTotal=true");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Get_recent_with_includeTotal_returns_paged_envelope()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var store = server.Services.GetRequiredService<IWebhookRejectionStore>();
        var flow = Guid.NewGuid();
        for (var i = 0; i < 12; i++)
            await store.WriteAsync(new WebhookRejectionRecord { FlowId = flow, Reason = "r" + i, ReceivedAt = DateTimeOffset.UtcNow });

        // Act — page 2 of 5/page
        var response = await server.Client.GetAsync("/flows/api/webhooks/recent?skip=5&take=5&includeTotal=true");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(5, doc.RootElement.GetProperty("items").GetArrayLength());
        Assert.True(doc.RootElement.GetProperty("total").GetInt32() >= 12);
        Assert.Equal(5, doc.RootElement.GetProperty("skip").GetInt32());
        Assert.Equal(5, doc.RootElement.GetProperty("take").GetInt32());
    }

    [Fact]
    public async Task Get_recent_without_paging_args_returns_bare_array_for_v1_25_compat()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var store = server.Services.GetRequiredService<IWebhookRejectionStore>();
        await store.WriteAsync(new WebhookRejectionRecord { FlowId = Guid.NewGuid(), Reason = "x", ReceivedAt = DateTimeOffset.UtcNow });

        // Act
        var response = await server.Client.GetAsync("/flows/api/webhooks/recent");

        // Assert — bare JSON array, not the paged envelope.
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task Get_recent_honours_Accept_Encoding_brotli()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var store = server.Services.GetRequiredService<IWebhookRejectionStore>();
        for (var i = 0; i < 50; i++)
            await store.WriteAsync(new WebhookRejectionRecord { FlowId = Guid.NewGuid(), Reason = "padding-payload-row-" + i, ReceivedAt = DateTimeOffset.UtcNow });
        var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/flows/api/webhooks/recent?take=50&includeTotal=true");
        req.Headers.AcceptEncoding.ParseAdd("br, gzip");

        // Act
        var response = await server.Client.SendAsync(req);

        // Assert — server picked Brotli, set Vary, body decompresses to valid JSON.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("br", response.Content.Headers.ContentEncoding.FirstOrDefault());
        Assert.Contains("Accept-Encoding", string.Join(",", response.Headers.Vary), StringComparison.OrdinalIgnoreCase);
        await using var raw = await response.Content.ReadAsStreamAsync();
        await using var brotli = new System.IO.Compression.BrotliStream(raw, System.IO.Compression.CompressionMode.Decompress);
        using var doc = await JsonDocument.ParseAsync(brotli);
        Assert.True(doc.RootElement.GetProperty("total").GetInt32() >= 50);
    }

    [Fact]
    public async Task Get_recent_honours_Accept_Encoding_gzip_when_brotli_absent()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var store = server.Services.GetRequiredService<IWebhookRejectionStore>();
        for (var i = 0; i < 20; i++)
            await store.WriteAsync(new WebhookRejectionRecord { FlowId = Guid.NewGuid(), Reason = "gz-pad-" + i, ReceivedAt = DateTimeOffset.UtcNow });
        var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/flows/api/webhooks/recent?take=20&includeTotal=true");
        req.Headers.AcceptEncoding.ParseAdd("gzip");

        // Act
        var response = await server.Client.SendAsync(req);

        // Assert
        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.FirstOrDefault());
        await using var raw = await response.Content.ReadAsStreamAsync();
        await using var gzip = new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress);
        using var doc = await JsonDocument.ParseAsync(gzip);
        Assert.True(doc.RootElement.GetProperty("total").GetInt32() >= 20);
    }

    [Fact]
    public async Task Get_stats_returns_counts_grouped_by_reason()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var store = server.Services.GetRequiredService<IWebhookRejectionStore>();
        var flow = Guid.Parse("44444444-4444-4444-4444-444444444444");
        await store.WriteAsync(new WebhookRejectionRecord { FlowId = flow, Reason = "replay", ReceivedAt = DateTimeOffset.UtcNow });
        await store.WriteAsync(new WebhookRejectionRecord { FlowId = flow, Reason = "replay", ReceivedAt = DateTimeOffset.UtcNow });
        await store.WriteAsync(new WebhookRejectionRecord { FlowId = flow, Reason = "rate_limited", ReceivedAt = DateTimeOffset.UtcNow });

        // Act
        var response = await server.Client.GetAsync("/flows/api/webhooks/stats");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var counts = doc.RootElement.GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("replay").GetInt64());
        Assert.Equal(1, counts.GetProperty("rate_limited").GetInt64());
        Assert.Equal(24, doc.RootElement.GetProperty("windowHours").GetInt32());
    }
}
