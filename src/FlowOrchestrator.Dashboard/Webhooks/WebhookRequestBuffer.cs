using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace FlowOrchestrator.Dashboard.Webhooks;

/// <summary>
/// Helper that buffers a webhook request body once and exposes both the raw
/// bytes (for HMAC signature verification) and the parsed payload (for the
/// trigger context). Reading is bounded by <see cref="WebhookSecurityOptions.MaxBodyBytes"/>.
/// </summary>
/// <remarks>
/// Two body formats are understood: JSON (the default) and
/// <c>application/x-www-form-urlencoded</c> (Twilio and other form-posting publishers). A form body
/// becomes a JSON object for the trigger: each field name maps to its value as a string, and a field
/// that repeats maps to an array of its values in request order.
/// </remarks>
internal static class WebhookRequestBuffer
{
    private const string FormMediaType = "application/x-www-form-urlencoded";

    /// <summary>Outcome of <see cref="ReadAsync"/>.</summary>
    public enum BufferStatus
    {
        /// <summary>Body read successfully.</summary>
        Ok,

        /// <summary>Body length exceeded the configured cap before reading completed.</summary>
        TooLarge,

        /// <summary>Body bytes are not valid JSON.</summary>
        InvalidJson,

        /// <summary>A form-encoded body could not be parsed (malformed, or over the form reader's field limits).</summary>
        InvalidForm,
    }

    /// <summary>Result of <see cref="ReadAsync"/> — keeps the raw bytes alongside the parsed payload.</summary>
    /// <param name="Status">Outcome of the read.</param>
    /// <param name="Bytes">The raw body bytes the signature covers.</param>
    /// <param name="Parsed">The trigger body: a <see cref="JsonElement"/> for JSON and form bodies, or <see langword="null"/> when empty.</param>
    public readonly record struct BufferedBody(BufferStatus Status, byte[] Bytes, object? Parsed)
    {
        /// <summary>
        /// The decoded form fields of an <c>application/x-www-form-urlencoded</c> body, for signature
        /// schemes that sign them (Twilio); <see langword="null"/> for any other body. A repeated field
        /// carries its values joined with <c>","</c>.
        /// </summary>
        public IReadOnlyDictionary<string, string>? FormFields { get; init; }
    }

    /// <summary>
    /// Reads the request body into a byte buffer (capped) and parses it as JSON, or as a form when the
    /// request declares <c>application/x-www-form-urlencoded</c>. Empty or absent bodies are returned as
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
        if (IsForm(request.ContentType))
            return ParseForm(bytes);

        // System.Text.Json accepts invalid UTF-8 inside a JSON string and only fails when the string is
        // transcoded (later, as a 500 in the engine); a JSON body must be UTF-8 (RFC 8259 §8.1).
        if (!System.Text.Unicode.Utf8.IsValid(bytes))
            return new BufferedBody(BufferStatus.InvalidJson, bytes, null);

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

    /// <summary>Whether <paramref name="contentType"/> names <c>application/x-www-form-urlencoded</c> (parameters such as <c>charset</c> allowed).</summary>
    private static bool IsForm(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return false;
        var mediaType = contentType.AsSpan();
        var semi = mediaType.IndexOf(';');
        if (semi >= 0)
            mediaType = mediaType[..semi];
        return mediaType.Trim().Equals(FormMediaType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decodes a form body (UTF-8, <c>+</c> as space, percent-escapes) into the trigger's JSON object and
    /// the verifier's field map.
    /// </summary>
    private static BufferedBody ParseForm(byte[] bytes)
    {
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> fields;
        try
        {
            // FormReader applies the ASP.NET Core form limits (1024 fields, 2 KiB keys); the byte cap
            // above already bounds the total. Exceeding a limit throws InvalidDataException.
            using var reader = new FormReader(Encoding.UTF8.GetString(bytes));
            fields = reader.ReadForm();
        }
        catch (InvalidDataException)
        {
            return new BufferedBody(BufferStatus.InvalidForm, bytes, null);
        }

        var body = new Dictionary<string, object>(fields.Count, StringComparer.Ordinal);
        var formFields = new Dictionary<string, string>(fields.Count, StringComparer.Ordinal);
        foreach (var (name, values) in fields)
        {
            body[name] = values.Count == 1 ? values[0] ?? string.Empty : values.ToArray();
            formFields[name] = values.ToString();
        }

        return new BufferedBody(BufferStatus.Ok, bytes, JsonSerializer.SerializeToElement(body))
        {
            FormFields = formFields,
        };
    }
}
