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
