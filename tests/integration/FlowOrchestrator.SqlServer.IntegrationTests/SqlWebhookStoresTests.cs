using Dapper;
using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer.Tests;

/// <summary>
/// Integration tests for the SQL Server-backed webhook hardening stores
/// (replay nonces + rejection / DLQ log) — pinned to a real SQL Server
/// container via <see cref="SqlServerFixture"/>.
/// </summary>
public sealed class SqlWebhookStoresTests : IClassFixture<SqlServerFixture>
{
    private readonly string _connectionString;
    private readonly SqlWebhookReplayStore _replay;
    private readonly SqlWebhookRejectionStore _dlq;

    public SqlWebhookStoresTests(SqlServerFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
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
    public async Task Replay_owner_can_forget_its_entry_and_the_nonce_is_registrable_again()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var nonce = "own-" + Guid.NewGuid().ToString("N");
        var owner = Guid.NewGuid();
        await _replay.TryRegisterAsync(flowId, "webhook", nonce, DateTimeOffset.UtcNow.AddHours(1), owner);

        // Act
        var forgotten = await _replay.TryForgetAsync(flowId, "webhook", nonce, owner);
        var again = await _replay.TryRegisterAsync(flowId, "webhook", nonce, DateTimeOffset.UtcNow.AddHours(1), Guid.NewGuid());

        // Assert
        Assert.True(forgotten);
        Assert.True(again);
    }

    [Fact]
    public async Task Replay_forget_by_another_or_losing_owner_leaves_the_entry_in_place()
    {
        // Arrange — the loser's register fails (replay); neither it nor a stranger owns the entry.
        var flowId = Guid.NewGuid();
        var nonce = "other-" + Guid.NewGuid().ToString("N");
        var winner = Guid.NewGuid();
        var loser = Guid.NewGuid();
        await _replay.TryRegisterAsync(flowId, "webhook", nonce, DateTimeOffset.UtcNow.AddHours(1), winner);
        var lost = await _replay.TryRegisterAsync(flowId, "webhook", nonce, DateTimeOffset.UtcNow.AddHours(1), loser);

        // Act
        var byLoser = await _replay.TryForgetAsync(flowId, "webhook", nonce, loser);
        var byStranger = await _replay.TryForgetAsync(flowId, "webhook", nonce, Guid.NewGuid());
        var stillReplay = await _replay.TryRegisterAsync(flowId, "webhook", nonce, DateTimeOffset.UtcNow.AddHours(1), Guid.NewGuid());

        // Assert
        Assert.False(lost);
        Assert.False(byLoser);
        Assert.False(byStranger);
        Assert.False(stillReplay);
    }

    [Fact]
    public async Task Replay_entries_without_a_known_owner_are_never_forgotten()
    {
        // Arrange — five-argument overload (owner nobody holds), an explicit empty token, and a row written the
        // way rows looked before the owner column existed (NULL).
        var flowId = Guid.NewGuid();
        var anonymous = "anon-" + Guid.NewGuid().ToString("N");
        var empty = "empty-" + Guid.NewGuid().ToString("N");
        var legacy = "legacy-" + Guid.NewGuid().ToString("N");
        await _replay.TryRegisterAsync(flowId, "webhook", anonymous, DateTimeOffset.UtcNow.AddHours(1));
        await _replay.TryRegisterAsync(flowId, "webhook", empty, DateTimeOffset.UtcNow.AddHours(1), Guid.Empty);
        await using (var conn = new SqlConnection(_connectionString))
        {
            await conn.ExecuteAsync("INSERT INTO [WebhookReplayNonces] ([FlowId], [TriggerKey], [Nonce], [ExpiresAt]) VALUES (@FlowId, 'webhook', @Nonce, @ExpiresAt);",
                new { FlowId = flowId, Nonce = legacy, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        }

        // Act
        var a = await _replay.TryForgetAsync(flowId, "webhook", anonymous, Guid.Empty);
        var e = await _replay.TryForgetAsync(flowId, "webhook", empty, Guid.Empty);
        var l = await _replay.TryForgetAsync(flowId, "webhook", legacy, Guid.NewGuid());

        // Assert
        Assert.False(a);
        Assert.False(e);
        Assert.False(l);
        Assert.False(await _replay.TryRegisterAsync(flowId, "webhook", legacy, DateTimeOffset.UtcNow.AddHours(1)));
    }

    [Fact]
    public async Task Replay_forget_finds_an_over_long_nonce_through_its_storage_form()
    {
        // Arrange — a nonce longer than the 256-character column is stored as its digest.
        var flowId = Guid.NewGuid();
        var nonce = new string('q', 5000);
        var owner = Guid.NewGuid();
        await _replay.TryRegisterAsync(flowId, "webhook", nonce, DateTimeOffset.UtcNow.AddHours(1), owner);

        // Act
        var forgotten = await _replay.TryForgetAsync(flowId, "webhook", nonce, owner);

        // Assert
        Assert.True(forgotten);
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

    [Fact]
    public async Task Rejection_search_is_windowed_uncounted_and_treats_brackets_literally()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var key = "hook[a]-" + Guid.NewGuid().ToString("N")[..8];
        await _dlq.WriteAsync(new WebhookRejectionRecord { FlowId = flowId, TriggerKey = key, Reason = "rate_limited", ReceivedAt = now.AddDays(-30) });
        for (var i = 0; i < 3; i++)
            await _dlq.WriteAsync(new WebhookRejectionRecord { FlowId = flowId, TriggerKey = key, Reason = "rate_limited", ReceivedAt = now.AddMinutes(-i) });
        // "[a]" would be a character class without escaping and match "hooka-…" too.
        await _dlq.WriteAsync(new WebhookRejectionRecord { FlowId = flowId, TriggerKey = "hooka-" + key[8..], Reason = "rate_limited", ReceivedAt = now });

        // Act
        var page = await _dlq.QueryAsync(new WebhookRejectionQuery(FlowId: flowId, Search: key, Take: 2) { ReceivedFrom = now.AddDays(-7), CountTotal = false });
        var all = await _dlq.QueryAsync(new WebhookRejectionQuery(FlowId: flowId, Search: key, Take: 10));

        // Assert
        Assert.Equal(2, page.Items.Count);
        Assert.True(page.HasMore);
        Assert.False(page.TotalCounted);
        Assert.Equal(4, all.Total);
        Assert.All(all.Items, r => Assert.Equal(key, r.TriggerKey));
    }
}
