using System.Security.Cryptography;
using System.Text;
using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.UnitTests.Signature;

/// <summary>
/// Vector tests for the built-in <see cref="WebhookSignatureScheme.StandardWebhooks"/> dialect
/// (<see href="https://github.com/standard-webhooks/standard-webhooks/blob/main/spec/standard-webhooks.md"/>):
/// <c>webhook-signature: v1,&lt;base64&gt; …</c> over <c>{webhook-id}.{webhook-timestamp}.{body}</c>, keyed with the
/// base64-decoded part after <c>whsec_</c>.
/// </summary>
/// <remarks>
/// The known-answer vectors are copied verbatim from the reference libraries' own test suites; the behavioural
/// cases (multiple signatures, other versions, malformed entries, prefix optional) mirror the cases those suites
/// pin, so a delivery the reference verifier accepts is accepted here and vice versa.
/// </remarks>
public sealed class StandardWebhooksVectorTests
{
    // Reference-library constants (Python test_webhooks.py, Go webhook_test.go, C# WebhookTest.cs, JS webhook.test.ts).
    // The secret is assembled at runtime so secret scanners do not flag the public sample value.
    private const string SecretBase64 = "MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private static readonly string Secret = "whsec_" + SecretBase64;
    private const string MsgId = "msg_p5jXN8AQM9LWM0D4loKWxJek";
    private const string Timestamp = "1614265330";
    private const string Payload = "{\"test\": 2432232314}";
    private const string ExpectedSignature = "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=";

    private static readonly WebhookSignatureSpec _spec =
        PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.StandardWebhooks)!;

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>Independent signer: base64 HMAC-SHA256 over <c>{id}.{ts}.{body}</c> with the decoded secret.</summary>
    private static string Sign(string secretBase64, string id, string ts, byte[] body)
    {
        var signed = Utf8(id + "." + ts + ".").Concat(body).ToArray();
        return "v1," + Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(secretBase64), signed));
    }

    private static WebhookSignatureContext Context(
        string signatureHeader,
        string? key = null,
        byte[]? body = null,
        string? id = MsgId,
        string? ts = Timestamp,
        string? previous = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (id is not null) headers["webhook-id"] = id;
        if (ts is not null) headers["webhook-timestamp"] = ts;
        if (signatureHeader.Length > 0) headers["webhook-signature"] = signatureHeader;
        return new WebhookSignatureContext
        {
            Body = body ?? Utf8(Payload),
            Headers = headers,
            Spec = _spec,
            HmacKey = key ?? Secret,
            HmacKeyPrevious = previous,
        };
    }

    [Fact]
    public void Reference_library_vector_is_accepted_and_reports_signed_timestamp_and_id()
    {
        // Arrange — DOC: libraries/python/tests/test_webhooks.py test_sign_function (same values in the Go, C# and
        // JS suites): whsec_MfKQ…LaSw, msg_p5jXN8AQM9LWM0D4loKWxJek, 1614265330, {"test": 2432232314}.
        var ctx = Context(ExpectedSignature);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid, $"rejected with {result.Reason}");
        Assert.False(result.UsedRotationKey);
        Assert.Equal(Timestamp, result.AuthenticatedTimestamp);
        Assert.Equal(MsgId, result.AuthenticatedNonce);
    }

    [Fact]
    public void Reference_library_binary_payload_vector_is_accepted()
    {
        // Arrange — DOC: libraries/javascript/src/webhook.test.ts "sign signs a binary payload byte for byte":
        // every byte value 0…255 (not valid UTF-8), same secret / id / timestamp → v1,sEcZ042G…ULhSI=, which the
        // file notes is the Go library's signature for the same inputs. Proves the body is signed as raw bytes.
        var body = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var ctx = Context("v1,sEcZ042G8Crqgxbybb3y0eDxQR3TcfYsk11I1XULhSI=", body: body);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid, $"rejected with {result.Reason}");
    }

    [Fact]
    public void Independent_signer_reproduces_the_reference_vector()
    {
        // Arrange — guards the test-side signer used below against drift from the published vector.

        // Act
        var signature = Sign(SecretBase64, MsgId, Timestamp, Utf8(Payload));

        // Assert
        Assert.Equal(ExpectedSignature, signature);
    }

    [Theory]
    [InlineData("whsec_")]
    [InlineData("")]
    public void Secret_is_accepted_with_and_without_the_whsec_prefix(string prefix)
    {
        // Arrange — reference suites: test_signature_verification_with_and_without_prefix.
        var ctx = Context(ExpectedSignature, key: prefix + SecretBase64);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid, $"rejected with {result.Reason}");
    }

    [Theory]
    [InlineData("body")]
    [InlineData("id")]
    [InlineData("timestamp")]
    public void Tampering_with_any_signed_part_is_rejected(string part)
    {
        // Arrange — the signature stays the genuine one; one of the three signed inputs changes.
        var ctx = part switch
        {
            "body" => Context(ExpectedSignature, body: Utf8("{\"test\": 2432232315}")),
            "id" => Context(ExpectedSignature, id: "msg_p5jXN8AQM9LWM0D4loKWxJel"),
            _ => Context(ExpectedSignature, ts: "1614265331"),
        };
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(WebhookSignatureFailureReason.DigestMismatch, result.Reason);
    }

    [Fact]
    public void Library_invalid_signature_case_is_rejected()
    {
        // Arrange — the reference suites' "invalid signature" case: last base64 character changed (…1OE= → …1OA=).
        var ctx = Context("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OA=");
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.Equal(WebhookSignatureFailureReason.DigestMismatch, result.Reason);
    }

    [Fact]
    public void Multiple_signatures_with_one_valid_are_accepted()
    {
        // Arrange — DOC: libraries/go/webhook_test.go "valid multi sig is valid": two invalid v1, one v2, and the
        // valid signature third.
        var header = string.Join(" ",
            "v1,Ceo5qEr07ixe2NLpvHk3FH9bwy/WavXrAFQ/9tdO6mc=",
            "v2,Ceo5qEr07ixe2NLpvHk3FH9bwy/WavXrAFQ/9tdO6mc=",
            ExpectedSignature,
            "v1,Ceo5qEr07ixe2NLpvHk3FH9bwy/WavXrAFQ/9tdO6mc=");
        var ctx = Context(header);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid, $"rejected with {result.Reason}");
    }

    [Fact]
    public void Other_version_entries_are_ignored_next_to_a_valid_v1()
    {
        // Arrange — the spec's asymmetric v2a and deprecated v1a entries are not HMAC; they must neither fail the
        // header nor be compared. The v2a value is the spec's own example signature.
        var header = "v1a,hnO3f9T8Ytu9HwrXslvumlUpqtNVqkhqw/enGzPCXe5BdqzCInXqYXFymVJaA7AZdpXwVLPo3mNl8EM+m7TBAg== " +
                     "v2a,kG9XaxZjAEpKB8KeQQb5G1Xp73YH5Rf5gxoWe7cg1kYMiyspdtZXe9w+dAwKeOurAwxfqmlOG/QR4m9s37DzAg== " +
                     ExpectedSignature;
        var ctx = Context(header);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid, $"rejected with {result.Reason}");
    }

    [Fact]
    public void Valid_digest_under_another_version_label_is_rejected()
    {
        // Arrange — DOC: libraries/go/webhook_test.go "valid signature with another version is invalid".
        var ctx = Context(ExpectedSignature.Replace("v1,", "v2,", StringComparison.Ordinal));
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(WebhookSignatureFailureReason.UnsupportedVersion, result.Reason);
    }

    [Theory]
    [InlineData("v1 ")]
    [InlineData(",,, v1,not,base64  ")]
    [InlineData("v1, v1,,, ")]
    public void Malformed_entries_before_a_valid_signature_are_skipped(string junk)
    {
        // Arrange — DOC: libraries/go/webhook_test.go "entry without a comma followed by a valid signature is valid"
        // and "malformed entry followed by a valid signature is valid"; C# WebhookTest.cs uses "v1, v1,,, ".
        var ctx = Context(junk + ExpectedSignature);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid, $"rejected with {result.Reason}");
    }

    [Theory]
    [InlineData("webhook-signature", WebhookSignatureFailureReason.MissingHeader)]
    [InlineData("webhook-id", WebhookSignatureFailureReason.MissingHeader)]
    [InlineData("webhook-timestamp", WebhookSignatureFailureReason.MissingTimestamp)]
    public void Missing_header_is_rejected(string header, WebhookSignatureFailureReason expected)
    {
        // Arrange — a missing timestamp reports the more precise MissingTimestamp, like Slack / Stripe.
        var ctx = header switch
        {
            "webhook-signature" => Context(string.Empty),
            "webhook-id" => Context(ExpectedSignature, id: null),
            _ => Context(ExpectedSignature, ts: null),
        };
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(expected, result.Reason);
    }

    [Theory]
    [InlineData("whsec_not base64!!")]
    [InlineData("whsec_")]
    [InlineData("whsec" + "_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaS")]
    public void Undecodable_secret_is_rejected_as_key_not_configured(string key)
    {
        // Arrange — not base64 / empty after the prefix / truncated (length not a multiple of 4).
        var ctx = Context(ExpectedSignature, key: key);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(WebhookSignatureFailureReason.KeyNotConfigured, result.Reason);
    }

    [Fact]
    public void Rotated_out_secret_is_accepted_with_the_rotation_flag()
    {
        // Arrange — current key is a different (valid) secret; the delivery was signed with the previous one.
        var ctx = Context(ExpectedSignature, key: "whsec_" + Convert.ToBase64String(new byte[24]), previous: Secret);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid, $"rejected with {result.Reason}");
        Assert.True(result.UsedRotationKey);
        Assert.Equal(MsgId, result.AuthenticatedNonce);
    }

    [Fact]
    public void Header_carrying_signatures_for_both_secrets_during_rotation_is_accepted_with_the_current_key()
    {
        // Arrange — a publisher rolling its secret sends one v1 entry per secret.
        const string newSecretBase64 = "bmV3LXNlY3JldC1rZXktMjQtYnl0ZXMh";
        var header = ExpectedSignature + " " + Sign(newSecretBase64, MsgId, Timestamp, Utf8(Payload));
        var ctx = Context(header, key: "whsec_" + newSecretBase64, previous: Secret);
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.True(result.IsValid, $"rejected with {result.Reason}");
        Assert.False(result.UsedRotationKey);
    }

    [Fact]
    public void Scheme_name_resolves_case_insensitively_and_Svix_is_not_an_alias()
    {
        // Arrange
        static WebhookSignatureSpec? Resolve(string name) => WebhookSignatureSpecResolver.Resolve(
            new Dictionary<string, object?> { ["webhookSignatureScheme"] = name }, customSchemes: null);

        // Act
        var canonical = Resolve("StandardWebhooks");
        var lower = Resolve("standardwebhooks");
        var svix = Resolve("Svix");

        // Assert
        Assert.Same(_spec, canonical);
        Assert.Same(_spec, lower);
        Assert.Null(svix);
    }

    [Fact]
    public void Custom_manifest_fields_can_describe_the_same_dialect()
    {
        // Arrange — the new manifest fields (webhookHmacKeyEncoding, webhookMessageIdHeader,
        // webhookIgnoreMalformedSignatureEntries) make the built-in strategy reachable from a Custom spec.
        var spec = WebhookSignatureSpecResolver.Resolve(new Dictionary<string, object?>
        {
            ["webhookSignatureScheme"] = "Custom",
            ["webhookSignatureHeader"] = "webhook-signature",
            ["webhookSignatureEncoding"] = "Base64",
            ["webhookHmacKeyEncoding"] = "Base64",
            ["webhookSignatureMultiValueDelimiter"] = " ",
            ["webhookSignatureKeyValueSeparator"] = ",",
            ["webhookSignatureValueKey"] = "v1",
            ["webhookTimestampHeader"] = "webhook-timestamp",
            ["webhookMessageIdHeader"] = "webhook-id",
            ["webhookSignedPayloadStrategy"] = "MessageIdTimestampBody",
            ["webhookRequireTimestamp"] = true,
            ["webhookAcceptMultipleSignatures"] = true,
            ["webhookIgnoreMalformedSignatureEntries"] = true,
        }, customSchemes: null)!;
        var ctx = Context("v1 " + ExpectedSignature) with { Spec = spec };
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.Equal(HmacKeyEncoding.Base64, spec.KeyEncoding);
        Assert.Equal("webhook-id", spec.MessageIdHeaderName);
        Assert.True(result.IsValid, $"rejected with {result.Reason}");
        Assert.Equal(MsgId, result.AuthenticatedNonce);
    }

    [Fact]
    public void Signed_content_is_id_dot_timestamp_dot_raw_body()
    {
        // Arrange
        var body = new byte[] { 0x7B, 0xFF, 0x00, 0x7D };

        // Act
        var signed = SignedPayloadBuilder.MessageIdTimestampBody("msg_1", "1614265330", body);

        // Assert
        Assert.Equal(Utf8("msg_1.1614265330.").Concat(body).ToArray(), signed);
    }

    [Fact]
    public void Existing_multi_value_schemes_still_fail_closed_on_malformed_entries()
    {
        // Arrange — IgnoreMalformedEntries is opt-in; Stripe keeps rejecting a segment without "=".
        var stripe = PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.Stripe)!;
        var ctx = new WebhookSignatureContext
        {
            Body = Utf8("{}"),
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Stripe-Signature"] = "t=1,garbage" },
            Spec = stripe,
            HmacKey = "whsec_test",
        };
        var verifier = new HmacSignatureVerifier();

        // Act
        var result = verifier.Verify(ctx);

        // Assert
        Assert.Equal(WebhookSignatureFailureReason.MalformedHeader, result.Reason);
    }
}
