using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Tests.Storage;

/// <summary>
/// <see cref="IdempotencyKeyStorage"/> is the single normalisation every built-in store applies, so a key
/// that does not fit the 256-character column still deduplicates, identically on every provider.
/// </summary>
public sealed class IdempotencyKeyStorageTests
{
    [Fact]
    public void Normalize_short_key_is_trimmed_and_lower_cased_as_before()
    {
        // Arrange
        const string key = "  Order-42  ";

        // Act
        var normalized = IdempotencyKeyStorage.Normalize(key);

        // Assert
        Assert.Equal("order-42", normalized);
    }

    [Fact]
    public void Normalize_key_of_exactly_the_column_length_is_kept_verbatim()
    {
        // Arrange
        var key = new string('k', IdempotencyKeyStorage.MaxStoredLength);

        // Act
        var normalized = IdempotencyKeyStorage.Normalize(key);

        // Assert
        Assert.Equal(key, normalized);
    }

    [Fact]
    public void Normalize_long_key_becomes_a_71_char_sha256_digest()
    {
        // Arrange
        var key = new string('k', IdempotencyKeyStorage.MaxStoredLength + 1);

        // Act
        var normalized = IdempotencyKeyStorage.Normalize(key);

        // Assert
        Assert.StartsWith(IdempotencyKeyStorage.DigestPrefix, normalized, StringComparison.Ordinal);
        Assert.Equal(71, normalized.Length);
        Assert.Matches("^sha256:[0-9a-f]{64}$", normalized);
    }

    [Fact]
    public void Normalize_long_keys_differing_only_by_case_or_padding_share_a_digest()
    {
        // Arrange
        var key = new string('k', 300);

        // Act
        var a = IdempotencyKeyStorage.Normalize(key);
        var b = IdempotencyKeyStorage.Normalize("\t" + key.ToUpperInvariant() + "  ");

        // Assert
        Assert.Equal(a, b);
    }

    [Fact]
    public void Normalize_distinct_long_keys_get_distinct_digests()
    {
        // Arrange
        var key = new string('k', 300);

        // Act
        var a = IdempotencyKeyStorage.Normalize(key);
        var b = IdempotencyKeyStorage.Normalize(key + "x");

        // Assert
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ToStorageForm_keeps_a_short_key_as_written_and_digests_a_long_one()
    {
        // Arrange
        var longKey = new string('K', 300);

        // Act
        var shortForm = IdempotencyKeyStorage.ToStorageForm("Order-42");
        var longForm = IdempotencyKeyStorage.ToStorageForm(longKey);
        var none = IdempotencyKeyStorage.ToStorageForm(null);

        // Assert
        Assert.Equal("Order-42", shortForm);
        Assert.Equal(IdempotencyKeyStorage.Normalize(longKey), longForm);
        Assert.Null(none);
    }
}
