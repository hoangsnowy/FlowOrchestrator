using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Dashboard.Security;

namespace FlowOrchestrator.Dashboard.UnitTests.Security;

/// <summary>
/// Locks down how <see cref="ManifestSecretRedactor"/> decides that a property or header name carries a
/// secret: names are normalised (separators dropped, case ignored) before the fragments are matched, and the
/// cheap text pre-check never lets through a name the per-name check would mask.
/// </summary>
public sealed class SecretNameNormalisationTests
{
    /// <summary>Credential names in the spellings real APIs, headers and SDKs use.</summary>
    public static TheoryData<string> CredentialNames =>
    [
        "X-Api-Key",
        "api-key",
        "apiKey",
        "API_KEY",
        "private_key",
        "privateKey",
        "hmac_key",
        "x-functions-key",
        "Ocp-Apim-Subscription-Key",
        "pwd",
        "db.password",
        "client secret",
        "client_secret",
        "aws_access_key_id",
        "AccountKey",
        "SharedAccessKey",
        "sas-url",
        "signing_key",
        "refresh-token",
        "X-Session-Id",
        "Proxy-Authorization",
        "Set-Cookie",
        "id_jwt",
        "ConnectionStrings.Default",
        "passphrase",
        "credentials",
    ];

    /// <summary>Ordinary names that must stay readable for a viewer.</summary>
    public static TheoryData<string> OrdinaryNames =>
    [
        "orderId",
        "url",
        "retries",
        "webhookSlug",
        "status",
        "disaster-recovery",
        "customer_name",
        "Content-Type",
        "X-Request-Id",
        "stepKey",
        "triggerKey",
        "monkey",
        "description",
    ];

    [Theory]
    [MemberData(nameof(CredentialNames))]
    public void Credential_names_are_secret_whatever_their_separators_and_case(string name)
    {
        // Arrange

        // Act
        var secret = ManifestSecretRedactor.IsSecretName(name);

        // Assert
        Assert.True(secret, name);
    }

    [Theory]
    [MemberData(nameof(OrdinaryNames))]
    public void Ordinary_names_stay_visible(string name)
    {
        // Arrange

        // Act
        var secret = ManifestSecretRedactor.IsSecretName(name);

        // Assert
        Assert.False(secret, name);
    }

    [Theory]
    [MemberData(nameof(CredentialNames))]
    public void Pre_check_never_skips_a_name_the_per_name_check_masks(string name)
    {
        // Arrange — the separators sit inside the name, so a raw-text scan for "apikey" would not see "api-key".
        var json = "{\"orderId\":\"8c1f-2026-11-24\",\"" + name + "\":\"value-under-test\"}";

        // Act
        var mayContain = ManifestSecretRedactor.MayContainSecretName(json);
        var redacted = ManifestSecretRedactor.RedactJson(json);

        // Assert
        Assert.True(mayContain, name);
        Assert.NotNull(redacted);
        Assert.DoesNotContain("value-under-test", redacted);
        Assert.Contains("8c1f-2026-11-24", redacted);
    }

    [Fact]
    public void Payload_with_separators_but_no_secret_name_is_returned_without_parsing()
    {
        // Arrange — GUIDs, dates and decimals put separators in almost every payload.
        const string json = """{"orderId":"3f2b8a1e-6c1d-4f0e-9a7b-2d4c6e8f0a1b","placedAt":"2026-11-24T10:00:00Z","total":12.5,"note":"ships next week"}""";

        // Act
        var mayContain = ManifestSecretRedactor.MayContainSecretName(json);
        var redacted = ManifestSecretRedactor.RedactJson(json);

        // Assert
        Assert.False(mayContain);
        Assert.Same(json, redacted);
    }

    [Fact]
    public void Name_longer_than_the_stack_buffer_is_still_normalised()
    {
        // Arrange
        var name = new string('x', 300) + "-api-key";

        // Act
        var secret = ManifestSecretRedactor.IsSecretName(name);

        // Assert
        Assert.True(secret);
    }

    [Fact]
    public void When_trace_reading_a_secret_name_has_its_resolved_values_masked()
    {
        // Arrange — "resolved" is the expression with the runtime values substituted in.
        const string trace = """{"expression":"@triggerHeaders()['X-Api-Key'] == 'expected'","resolved":"'live-key-0123' == 'expected'","result":false}""";

        // Act
        var redacted = ManifestSecretRedactor.RedactEvaluationTrace(trace);

        // Assert
        Assert.NotNull(redacted);
        Assert.DoesNotContain("live-key-0123", redacted);
        Assert.Contains("X-Api-Key", redacted);
        Assert.Contains(ManifestSecretRedactor.Mask, redacted);
    }

    [Fact]
    public void When_trace_reading_ordinary_values_is_left_as_stored()
    {
        // Arrange
        const string trace = """{"expression":"@triggerBody()?.status == 'paid'","resolved":"'open' == 'paid'","result":false}""";

        // Act
        var redacted = ManifestSecretRedactor.RedactEvaluationTrace(trace);

        // Assert
        Assert.Same(trace, redacted);
    }

    [Fact]
    public void Run_copy_masks_step_and_attempt_when_traces()
    {
        // Arrange
        const string trace = """{"expression":"@triggerBody()?.private_key != ''","resolved":"'-----BEGIN PRIVATE KEY-----' != ''","result":false}""";
        var run = new FlowRunRecord
        {
            Id = Guid.NewGuid(),
            Status = "Succeeded",
            Steps =
            [
                new FlowStepRecord
                {
                    StepKey = "upload",
                    StepType = "Gcs",
                    Status = "Skipped",
                    EvaluationTraceJson = trace,
                    Attempts = [new FlowStepAttemptRecord { StepKey = "upload", StepType = "Gcs", Status = "Skipped", EvaluationTraceJson = trace }],
                },
            ],
        };

        // Act
        var copy = ManifestSecretRedactor.Redacted(run);

        // Assert
        var step = Assert.Single(copy.Steps!);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", step.EvaluationTraceJson);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", Assert.Single(step.Attempts!).EvaluationTraceJson);
        Assert.Contains("BEGIN PRIVATE KEY", run.Steps![0].EvaluationTraceJson);
    }

    [Fact]
    public void Header_map_masks_hyphenated_credential_headers()
    {
        // Arrange
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Api-Key"] = "k-1",
            ["Ocp-Apim-Subscription-Key"] = "k-2",
            ["x-functions-key"] = "k-3",
            ["X-Request-Id"] = "req-9",
        };

        // Act
        var masked = ManifestSecretRedactor.MaskHeaders(headers)!;

        // Assert
        Assert.Equal(ManifestSecretRedactor.Mask, masked["X-Api-Key"]);
        Assert.Equal(ManifestSecretRedactor.Mask, masked["Ocp-Apim-Subscription-Key"]);
        Assert.Equal(ManifestSecretRedactor.Mask, masked["x-functions-key"]);
        Assert.Equal("req-9", masked["X-Request-Id"]);
    }
}
