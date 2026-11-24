using System.Security.Cryptography;
using System.Text;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Tests.Storage;

/// <summary>
/// <see cref="WebhookReplayNonceStorage"/> bounds a replay nonce to the 256-character column of the SQL stores
/// while keeping a long nonce's replay protection: the same long nonce always maps to the same stored key.
/// </summary>
public sealed class WebhookReplayNonceStorageTests
{
    [Fact]
    public void Nonce_that_fits_the_column_is_stored_verbatim()
    {
        // Arrange — no trimming or case folding: a nonce is an opaque token.
        var nonce = " Delivery-" + new string('N', WebhookReplayNonceStorage.MaxStoredLength - 10);

        // Act
        var stored = WebhookReplayNonceStorage.ToStorageForm(nonce);

        // Assert
        Assert.Equal(WebhookReplayNonceStorage.MaxStoredLength, nonce.Length);
        Assert.Same(nonce, stored);
    }

    [Fact]
    public void Nonce_longer_than_the_column_is_stored_as_its_sha256_digest()
    {
        // Arrange
        var nonce = new string('d', WebhookReplayNonceStorage.MaxStoredLength + 1);
        var expected = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce))).ToLowerInvariant();

        // Act
        var stored = WebhookReplayNonceStorage.ToStorageForm(nonce);

        // Assert
        Assert.Equal(expected, stored);
        Assert.Equal(71, stored.Length);
    }

    [Fact]
    public void Same_long_nonce_maps_to_one_key_and_different_ones_do_not_collide()
    {
        // Arrange
        var first = new string('a', 5000);
        var second = new string('a', 4999) + "b";

        // Act
        var firstAgain = WebhookReplayNonceStorage.ToStorageForm(new string('a', 5000));
        var firstStored = WebhookReplayNonceStorage.ToStorageForm(first);
        var secondStored = WebhookReplayNonceStorage.ToStorageForm(second);

        // Assert
        Assert.Equal(firstStored, firstAgain);
        Assert.NotEqual(firstStored, secondStored);
    }

    [Fact]
    public void Storage_form_is_idempotent()
    {
        // Arrange — the gate and the built-in stores both apply it.
        var once = WebhookReplayNonceStorage.ToStorageForm(new string('x', 1000));

        // Act
        var twice = WebhookReplayNonceStorage.ToStorageForm(once);

        // Assert
        Assert.Equal(once, twice);
    }
}
