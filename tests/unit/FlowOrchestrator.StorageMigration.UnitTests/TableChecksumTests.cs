using FlowOrchestrator.StorageMigration.Internal;
using FlowOrchestrator.StorageMigration.Schema;

namespace FlowOrchestrator.StorageMigration.UnitTests;

/// <summary>
/// The checksum is what lets the verification say "the target reads back equal": it must be independent of
/// row order (the two backends sort GUIDs and text differently), equal across the documented precision loss,
/// and different for any real change of a value.
/// </summary>
public sealed class TableChecksumTests
{
    private static readonly StorageTable Runs = StorageCatalog.FlowRuns;

    private static object?[] Run(Guid id, string status = "Succeeded", DateTimeOffset? started = null, string? trigger = "{}") =>
        [id, Guid.Empty, "Flow", status, "manual", trigger, "job-1", started ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, null];

    private static string Sum(params object?[][] rows)
    {
        var checksum = new TableChecksum();
        foreach (var row in rows)
            checksum.Add(Runs, row);
        return checksum.Value;
    }

    [Fact]
    public void Row_order_does_not_change_the_checksum()
    {
        // Arrange
        var a = Run(Guid.NewGuid());
        var b = Run(Guid.NewGuid(), "Failed");
        var c = Run(Guid.NewGuid(), "Cancelled");

        // Act
        var forward = Sum(a, b, c);
        var backward = Sum(c, a, b);

        // Assert
        Assert.Equal(forward, backward);
    }

    [Fact]
    public void A_timestamp_truncated_to_the_microsecond_hashes_equal_to_the_100ns_original()
    {
        // Arrange — what SQL Server holds vs what PostgreSQL holds after the copy.
        var id = Guid.NewGuid();
        var sqlServer = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(7)).AddTicks(1_234_567);
        var postgres = ValueNormalizer.ToPostgresPrecision(sqlServer);

        // Act
        var source = Sum(Run(id, started: sqlServer));
        var target = Sum(Run(id, started: postgres));

        // Assert
        Assert.Equal(source, target);
    }

    [Fact]
    public void The_same_instant_in_a_different_offset_hashes_equal()
    {
        // Arrange
        var id = Guid.NewGuid();
        var local = new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.FromHours(7));

        // Act
        var withOffset = Sum(Run(id, started: local));
        var inUtc = Sum(Run(id, started: local.ToUniversalTime()));

        // Assert
        Assert.Equal(withOffset, inUtc);
    }

    [Fact]
    public void A_one_microsecond_difference_changes_the_checksum()
    {
        // Arrange
        var id = Guid.NewGuid();
        var started = new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

        // Act
        var before = Sum(Run(id, started: started));
        var after = Sum(Run(id, started: started.AddTicks(10)));

        // Assert
        Assert.NotEqual(before, after);
    }

    [Theory]
    [InlineData("{}", "{ }")]
    [InlineData("abc", "ABC")]
    [InlineData("", null)]
    [InlineData("a", "a ")]
    public void Any_change_of_a_text_value_changes_the_checksum(string? left, string? right)
    {
        // Arrange
        var id = Guid.NewGuid();

        // Act
        var a = Sum(Run(id, trigger: left));
        var b = Sum(Run(id, trigger: right));

        // Assert
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Two_rows_swapping_a_value_do_not_cancel_out()
    {
        // Arrange — the same multiset of values distributed differently over the rows.
        var x = Guid.NewGuid();
        var y = Guid.NewGuid();

        // Act
        var original = Sum(Run(x, "Succeeded"), Run(y, "Failed"));
        var swapped = Sum(Run(x, "Failed"), Run(y, "Succeeded"));

        // Assert
        Assert.NotEqual(original, swapped);
    }

    [Fact]
    public void Counts_rows_and_renders_32_hex_digits()
    {
        // Arrange
        var checksum = new TableChecksum();

        // Act
        checksum.Add(Runs, Run(Guid.NewGuid()));
        checksum.Add(Runs, Run(Guid.NewGuid()));

        // Assert
        Assert.Equal(2, checksum.Count);
        Assert.Matches("^[0-9a-f]{32}$", checksum.Value);
    }

    [Fact]
    public void An_empty_table_has_the_zero_checksum()
    {
        // Arrange
        var checksum = new TableChecksum();

        // Act
        var value = checksum.Value;

        // Assert
        Assert.Equal(new string('0', 32), value);
        Assert.Equal(0, checksum.Count);
    }

    [Fact]
    public void Booleans_integers_and_doubles_hash_by_value()
    {
        // Arrange
        var table = StorageCatalog.WebhookRejections;
        object?[] Row(double? ms, bool accepted, int status) =>
            [7L, null, "hook", DateTimeOffset.UnixEpoch, "127.0.0.1", "accepted", status, 10, null, null, "hmac", ms, accepted];

        // Act
        var baseline = new TableChecksum();
        baseline.Add(table, Row(1.5, true, 200));
        var otherDouble = new TableChecksum();
        otherDouble.Add(table, Row(1.5000001, true, 200));
        var otherBool = new TableChecksum();
        otherBool.Add(table, Row(1.5, false, 200));
        var same = new TableChecksum();
        same.Add(table, Row(1.5, true, 200));

        // Assert
        Assert.Equal(baseline.Value, same.Value);
        Assert.NotEqual(baseline.Value, otherDouble.Value);
        Assert.NotEqual(baseline.Value, otherBool.Value);
    }
}
