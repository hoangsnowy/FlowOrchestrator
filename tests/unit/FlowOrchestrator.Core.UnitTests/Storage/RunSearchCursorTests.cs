using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Tests.Storage;

/// <summary>
/// <see cref="RunSearchCursor"/> is the opaque keyset token the dashboard round-trips through
/// <c>?after=</c>: it must survive encoding exactly, reject anything it did not produce, and order rows
/// the way the in-memory providers page them.
/// </summary>
public sealed class RunSearchCursorTests
{
    [Fact]
    public void Encode_then_TryParse_round_trips_the_instant_and_the_id()
    {
        // Arrange
        var cursor = new RunSearchCursor(new DateTimeOffset(2026, 11, 23, 8, 15, 42, TimeSpan.FromHours(7)).AddTicks(1234567), Guid.NewGuid());

        // Act
        var token = cursor.Encode();
        var parsed = RunSearchCursor.TryParse(token, out var decoded);

        // Assert
        Assert.True(parsed);
        Assert.Equal(cursor.StartedAt.UtcTicks, decoded.StartedAt.UtcTicks);
        Assert.Equal(TimeSpan.Zero, decoded.StartedAt.Offset);
        Assert.Equal(cursor.Id, decoded.Id);
    }

    [Fact]
    public void Encode_is_url_safe_and_short()
    {
        // Arrange
        var cursor = new RunSearchCursor(DateTimeOffset.MaxValue, new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff"));

        // Act
        var token = cursor.Encode();

        // Assert — base64url without padding: nothing a query string must escape.
        Assert.True(token.Length <= RunSearchCursor.MaxEncodedLength);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a cursor")]
    [InlineData("AAAA")]
    [InlineData("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%")]
    public void TryParse_rejects_malformed_input(string value)
    {
        // Arrange

        // Act
        var parsed = RunSearchCursor.TryParse(value, out var cursor);

        // Assert
        Assert.False(parsed);
        Assert.Equal(default, cursor);
    }

    [Fact]
    public void TryParse_rejects_an_unknown_format_version()
    {
        // Arrange — a 25-byte payload whose version byte is not 1.
        var bytes = new byte[25];
        bytes[0] = 2;
        var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // Act
        var parsed = RunSearchCursor.TryParse(token, out _);

        // Assert
        Assert.False(parsed);
    }

    [Fact]
    public void TryParse_rejects_over_long_input_without_decoding()
    {
        // Arrange
        var token = new string('A', RunSearchCursor.MaxEncodedLength + 1);

        // Act
        var parsed = RunSearchCursor.TryParse(token, out _);

        // Assert
        Assert.False(parsed);
    }

    [Fact]
    public void Precedes_orders_by_start_time_descending_then_id_ascending()
    {
        // Arrange
        var at = new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);
        var low = new Guid("00000000-0000-0000-0000-000000000001");
        var high = new Guid("00000000-0000-0000-0000-000000000009");
        var cursor = new RunSearchCursor(at, low);

        // Act
        var older = cursor.Precedes(new FlowRunRecord { Id = low, StartedAt = at.AddTicks(-1) });
        var newer = cursor.Precedes(new FlowRunRecord { Id = high, StartedAt = at.AddTicks(1) });
        var tieAfter = cursor.Precedes(new FlowRunRecord { Id = high, StartedAt = at });
        var self = cursor.Precedes(new FlowRunRecord { Id = low, StartedAt = at });

        // Assert
        Assert.True(older);
        Assert.False(newer);
        Assert.True(tieAfter);
        Assert.False(self);
    }

    [Fact]
    public void After_takes_the_rows_start_time_and_id()
    {
        // Arrange
        var run = new FlowRunRecord { Id = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow };

        // Act
        var cursor = RunSearchCursor.After(run);

        // Assert
        Assert.Equal(run.Id, cursor.Id);
        Assert.Equal(run.StartedAt, cursor.StartedAt);
    }
}
