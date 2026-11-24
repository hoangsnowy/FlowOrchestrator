namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// Replay nonces longer than the <c>nonce VARCHAR(256)</c> column: the store must answer with a verdict
/// (never SQLSTATE 22001, which surfaced as a 500 from the webhook endpoint), and a long nonce must still be
/// detected as a replay.
/// </summary>
public sealed class PostgreSqlWebhookReplayNonceLengthTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlWebhookReplayStore _replay;

    /// <summary>Binds the store to the shared container.</summary>
    /// <param name="fixture">The database container fixture.</param>
    public PostgreSqlWebhookReplayNonceLengthTests(PostgreSqlFixture fixture)
    {
        _replay = new PostgreSqlWebhookReplayStore(fixture.ConnectionString);
    }

    [Fact]
    public async Task TryRegister_with_nonce_longer_than_column_does_not_throw()
    {
        // Arrange
        var nonce = new string('n', 300);
        var expires = DateTimeOffset.UtcNow.AddMinutes(10);

        // Act
        var ex = await Record.ExceptionAsync(() => _replay.TryRegisterAsync(Guid.NewGuid(), "webhook", nonce, expires).AsTask());

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public async Task Long_nonce_registered_twice_is_reported_as_seen()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var nonce = new string('n', 5000);
        var expires = DateTimeOffset.UtcNow.AddMinutes(10);

        // Act
        var first = await _replay.TryRegisterAsync(flowId, "webhook", nonce, expires);
        var second = await _replay.TryRegisterAsync(flowId, "webhook", nonce, expires);
        var other = await _replay.TryRegisterAsync(flowId, "webhook", new string('n', 4999) + "x", expires);

        // Assert
        Assert.True(first);
        Assert.False(second);
        Assert.True(other);
    }
}
