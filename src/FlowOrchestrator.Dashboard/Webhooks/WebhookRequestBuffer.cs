using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace FlowOrchestrator.Dashboard.Webhooks;

/// <summary>
/// Helper that buffers a webhook request body once and exposes both the raw
/// bytes (for HMAC signature verification) and the parsed JSON payload (for
/// trigger context). Reading is bounded by <see cref="WebhookSecurityOptions.MaxBodyBytes"/>.
/// </summary>
public static class WebhookRequestBuffer
{
    /// <summary>Outcome of <see cref="ReadAsync"/>.</summary>
    public enum BufferStatus
    {
        /// <summary>Body read successfully.</summary>
        Ok,

        /// <summary>Body length exceeded the configured cap before reading completed.</summary>
        TooLarge,

        /// <summary>Body bytes are not valid JSON.</summary>
        InvalidJson,
    }

    /// <summary>Result of <see cref="ReadAsync"/> — keeps the raw bytes alongside the parsed payload.</summary>
    public readonly record struct BufferedBody(BufferStatus Status, byte[] Bytes, object? Parsed);

    /// <summary>
    /// Reads the request body into a byte buffer (capped) and deserialises the
    /// JSON payload. Empty or absent bodies are returned as
    /// <see cref="BufferStatus.Ok"/> with <see cref="BufferedBody.Bytes"/> empty
    /// and <see cref="BufferedBody.Parsed"/> <see langword="null"/>.
    /// </summary>
    /// <param name="request">Incoming HTTP request.</param>
    /// <param name="maxBytes">Hard cap on body size (bytes).</param>
    /// <param name="cancellationToken">Cancellation propagated from the host pipeline.</param>
    public static async ValueTask<BufferedBody> ReadAsync(
        HttpRequest request,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        if (request.ContentLength == 0)
            return new BufferedBody(BufferStatus.Ok, Array.Empty<byte>(), null);

        if (request.ContentLength is { } declaredLength && declaredLength > maxBytes)
            return new BufferedBody(BufferStatus.TooLarge, Array.Empty<byte>(), null);

        // Stream into a memory buffer; refuse early if we cross the cap.
        using var ms = new MemoryStream(capacity: (int)Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[8192];
        var totalRead = 0L;
        int read;
        while ((read = await request.Body.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            totalRead += read;
            if (totalRead > maxBytes)
                return new BufferedBody(BufferStatus.TooLarge, Array.Empty<byte>(), null);
            ms.Write(buffer, 0, read);
        }

        if (ms.Length == 0)
            return new BufferedBody(BufferStatus.Ok, Array.Empty<byte>(), null);

        var bytes = ms.ToArray();
        try
        {
            var parsed = JsonSerializer.Deserialize<object>(bytes);
            return new BufferedBody(BufferStatus.Ok, bytes, parsed);
        }
        catch (JsonException)
        {
            return new BufferedBody(BufferStatus.InvalidJson, bytes, null);
        }
    }
}
