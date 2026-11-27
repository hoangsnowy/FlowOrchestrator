using Dapper;
using FlowOrchestrator.Core.Storage;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// Integration tests for the PostgreSQL-backed webhook hardening stores
/// (replay nonces + rejection / DLQ log) — pinned to a real Postgres
/// container via <see cref="PostgreSqlFixture"/>.
/// </summary>
public sealed class PostgreSqlWebhookStoresTests : IClassFixture<PostgreSqlFixture>
{
    private readonly string _connectionString;
    private readonly PostgreSqlWebhookReplayStore _replay;
    private readonly PostgreSqlWebhookRejectionStore _dlq;

    public PostgreSqlWebhookStoresTests(PostgreSqlFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
        _replay = new PostgreSqlWebhookReplayStore(fixture.ConnectionString);
        _dlq = new PostgreSqlWebhookRejectionStore(fixture.ConnectionString);
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

        // Assert
        Assert.True(removed >= 1);
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
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.ExecuteAsync("INSERT INTO webhook_replay_nonces (flow_id, trigger_key, nonce, expires_at) VALUES (@FlowId, 'webhook', @Nonce, @ExpiresAt);",
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
}
