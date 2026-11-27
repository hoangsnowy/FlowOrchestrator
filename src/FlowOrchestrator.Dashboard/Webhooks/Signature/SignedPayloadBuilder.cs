using System.Text;

namespace FlowOrchestrator.Dashboard.Webhooks.Signature;

/// <summary>
/// Pure helpers that materialise the byte sequence covered by an HMAC digest.
/// Each method maps to one <see cref="SignedPayloadStrategy"/> value so that
/// <see cref="HmacSignatureVerifier"/> can dispatch on the spec without taking
/// a delegate dependency for the built-in strategies.
/// </summary>
internal static class SignedPayloadBuilder
{
    /// <summary>Returns the body bytes verbatim (no allocation in the typical path).</summary>
    /// <param name="body">Raw HTTP body bytes.</param>
    public static byte[] RawBody(ReadOnlyMemory<byte> body) => body.ToArray();

    /// <summary>Builds <c>{timestamp}{delimiter}{body}</c> (Stripe / Calendly use <c>"."</c>).</summary>
    /// <param name="timestamp">Unix-seconds timestamp string parsed from the multi-value header.</param>
    /// <param name="body">Raw HTTP body bytes.</param>
    /// <param name="delimiter">Delimiter between timestamp and body.</param>
    public static byte[] TimestampDotBody(string timestamp, ReadOnlyMemory<byte> body, string delimiter = ".")
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp + delimiter);
        var output = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, output, 0, prefix.Length);
        body.Span.CopyTo(output.AsSpan(prefix.Length));
        return output;
    }

    /// <summary>Builds <c>{version}{delim}{timestamp}{delim}{body}</c> (Slack <c>v0:ts:body</c>).</summary>
    /// <param name="version">Version literal (e.g. <c>"v0"</c>).</param>
    /// <param name="timestamp">Unix-seconds timestamp string from <c>X-Slack-Request-Timestamp</c> or equivalent.</param>
    /// <param name="body">Raw HTTP body bytes.</param>
    /// <param name="delimiter">Delimiter between fields, default <c>":"</c>.</param>
    public static byte[] ColonDelimited(string version, string timestamp, ReadOnlyMemory<byte> body, string delimiter = ":")
    {
        var prefix = Encoding.UTF8.GetBytes(version + delimiter + timestamp + delimiter);
        var output = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, output, 0, prefix.Length);
        body.Span.CopyTo(output.AsSpan(prefix.Length));
        return output;
    }

    /// <summary>
    /// Builds <c>{messageId}{delim}{timestamp}{delim}{body}</c> (Standard Webhooks <c>msg_id.timestamp.payload</c>).
    /// </summary>
    /// <param name="messageId">Message id from the <c>webhook-id</c> header (or the spec's configured id header).</param>
    /// <param name="timestamp">Unix-seconds timestamp string from <c>webhook-timestamp</c>.</param>
    /// <param name="body">Raw HTTP body bytes, signed byte for byte (no UTF-8 round trip).</param>
    /// <param name="delimiter">Delimiter between fields, default <c>"."</c>.</param>
    /// <remarks>
    /// Encodes the three parts straight into one output buffer: a single allocation per verification, no
    /// intermediate concatenated prefix string.
    /// </remarks>
    public static byte[] MessageIdTimestampBody(string messageId, string timestamp, ReadOnlyMemory<byte> body, string delimiter = ".")
    {
        var utf8 = Encoding.UTF8;
        var idLength = utf8.GetByteCount(messageId);
        var tsLength = utf8.GetByteCount(timestamp);
        var delimLength = utf8.GetByteCount(delimiter);
        var output = new byte[idLength + delimLength + tsLength + delimLength + body.Length];
        var span = output.AsSpan();
        var written = utf8.GetBytes(messageId, span);
        written += utf8.GetBytes(delimiter, span[written..]);
        written += utf8.GetBytes(timestamp, span[written..]);
        written += utf8.GetBytes(delimiter, span[written..]);
        body.Span.CopyTo(span[written..]);
        return output;
    }

    /// <summary>Builds <c>{absoluteUrl}{sortedFormFieldConcatenation}</c> (Twilio).</summary>
    /// <param name="absoluteUrl">Absolute request URL including scheme + host + path.</param>
    /// <param name="formFields">Form-encoded fields parsed from the body. Sorted by key, ordinal.</param>
    /// <remarks>
    /// Twilio sorts form fields by key (ordinal) and concatenates <c>{key}{value}</c>
    /// pairs with no separator. Empty values are kept; missing fields contribute nothing.
    /// </remarks>
    public static byte[] UrlPlusSortedForm(string absoluteUrl, IReadOnlyDictionary<string, string> formFields)
    {
        var sb = new StringBuilder(absoluteUrl);
        foreach (var (key, value) in formFields.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(key);
            sb.Append(value);
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Builds <c>{absoluteUrl}{body}</c> (Square).</summary>
    /// <param name="absoluteUrl">Absolute notification URL configured for the publisher.</param>
    /// <param name="body">Raw HTTP body bytes.</param>
    public static byte[] UrlPlusBody(string absoluteUrl, ReadOnlyMemory<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(absoluteUrl);
        var output = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, output, 0, prefix.Length);
        body.Span.CopyTo(output.AsSpan(prefix.Length));
        return output;
    }

    /// <summary>Builds <c>{timestamp}{token}</c> (Mailgun signature lives in the body, covers ts+token).</summary>
    /// <param name="timestamp">Mailgun <c>signature.timestamp</c> string from the JSON body.</param>
    /// <param name="token">Mailgun <c>signature.token</c> string from the JSON body.</param>
    public static byte[] TimestampPlusToken(string timestamp, string token) =>
        Encoding.UTF8.GetBytes(timestamp + token);
}
