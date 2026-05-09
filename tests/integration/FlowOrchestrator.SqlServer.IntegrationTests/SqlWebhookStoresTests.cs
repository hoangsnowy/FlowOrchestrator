using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.SqlServer.Tests;

/// <summary>
/// Integration tests for the SQL Server-backed webhook hardening stores
/// (replay nonces + rejection / DLQ log) — pinned to a real SQL Server
/// container via <see cref="SqlServerFixture"/>.
/// </summary>
public sealed class SqlWebhookStoresTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlWebhookReplayStore _replay;
    private readonly SqlWebhookRejectionStore _dlq;

    public SqlWebhookStoresTests(SqlServerFixture fixture)
    {
        _replay = new SqlWebhookReplayStore(fixture.ConnectionString);
        _dlq = new SqlWebhookRejectionStore(fixture.ConnectionString);
    }

    [Fact]
    public async Task Replay_first_register_returns_true_and_dup_returns_false()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var nonce = "nonce-" + Guid.NewGuid().ToString("N");

        // Act
        var first = await _replay.TryRegisterAsync(flowId, "webhook", nonce, DateTimeOffset.UtcNow.AddHours(1));
        var second = await _replay.TryRegisterAsync(flowId, "webhook", nonce, DateTimeOffset.UtcNow.AddHours(1));

        // Assert
        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public async Task Replay_purge_removes_only_expired_entries()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var keep = "k-" + Guid.NewGuid().ToString("N");
        var drop = "d-" + Guid.NewGuid().ToString("N");
        await _replay.TryRegisterAsync(flowId, "webhook", keep, DateTimeOffset.UtcNow.AddHours(1));
        await _replay.TryRegisterAsync(flowId, "webhook", drop, DateTimeOffset.UtcNow.AddSeconds(-10));

        // Act
        var removed = await _replay.PurgeExpiredAsync(DateTimeOffset.UtcNow);

        // Assert — at least our one stale entry was removed.
        Assert.True(removed >= 1);

        // The kept nonce still triggers a duplicate response.
        var dup = await _replay.TryRegisterAsync(flowId, "webhook", keep, DateTimeOffset.UtcNow.AddHours(1));
        Assert.False(dup);
    }

    [Fact]
    public async Task Dlq_write_then_query_returns_the_row()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        await _dlq.WriteAsync(new WebhookRejectionRecord
        {
            FlowId = flowId,
            TriggerKey = "webhook",
            Reason = "signature_invalid",
            ReceivedAt = DateTimeOffset.UtcNow,
            StatusCode = 401,
            BodyBytes = 12,
            IsAccepted = false,
        });

        // Act
        var rows = await _dlq.QueryRecentAsync(flowId, reason: null, includeAccepted: false, skip: 0, take: 50);

        // Assert
        Assert.Single(rows);
        Assert.Equal("signature_invalid", rows[0].Reason);
        Assert.True(rows[0].Id > 0);
    }

    [Fact]
    public async Task Dlq_counts_by_reason_groups_correctly()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
        {
            await _dlq.WriteAsync(new WebhookRejectionRecord
            {
                FlowId = flowId,
                Reason = "replay",
                ReceivedAt = DateTimeOffset.UtcNow,
                StatusCode = 409,
                BodyBytes = 5,
            });
        }
        await _dlq.WriteAsync(new WebhookRejectionRecord
        {
            FlowId = flowId,
            Reason = "rate_limited",
            ReceivedAt = DateTimeOffset.UtcNow,
            StatusCode = 429,
            BodyBytes = 5,
        });

        // Act
        var counts = await _dlq.CountsByReasonAsync(TimeSpan.FromHours(24));

        // Assert
        Assert.True(counts["replay"] >= 3);
        Assert.True(counts["rate_limited"] >= 1);
    }
}
