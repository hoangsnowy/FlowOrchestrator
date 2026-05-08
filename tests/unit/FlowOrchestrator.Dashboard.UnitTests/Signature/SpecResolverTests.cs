using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.UnitTests.Signature;

/// <summary>Tests <see cref="WebhookSignatureSpecResolver"/> manifest-input precedence.</summary>
public sealed class SpecResolverTests
{
    [Fact]
    public void Returns_null_when_no_scheme_set()
    {
        // Arrange
        var inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // Act
        var spec = WebhookSignatureSpecResolver.Resolve(inputs, customSchemes: null);

        // Assert
        Assert.Null(spec);
    }

    [Fact]
    public void Resolves_built_in_enum_value_case_insensitively()
    {
        // Arrange
        var inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["webhookSignatureScheme"] = "github",
        };

        // Act
        var spec = WebhookSignatureSpecResolver.Resolve(inputs, customSchemes: null);

        // Assert
        Assert.NotNull(spec);
        Assert.Equal("X-Hub-Signature-256", spec!.HeaderName);
    }

    [Fact]
    public void Custom_overlay_shadows_built_in()
    {
        // Arrange
        var inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["webhookSignatureScheme"] = "GitHub",
        };
        var custom = new Dictionary<string, WebhookSignatureSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["GitHub"] = new WebhookSignatureSpec { HeaderName = "X-Override", SignedPayloadStrategy = SignedPayloadStrategy.RawBody },
        };

        // Act
        var spec = WebhookSignatureSpecResolver.Resolve(inputs, custom);

        // Assert
        Assert.NotNull(spec);
        Assert.Equal("X-Override", spec!.HeaderName);
    }

    [Fact]
    public void Custom_scheme_builds_from_individual_fields()
    {
        // Arrange
        var inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["webhookSignatureScheme"] = "Custom",
            ["webhookSignatureHeader"] = "X-MyHook-Signature",
            ["webhookSignatureAlgorithm"] = "sha512",
            ["webhookSignatureEncoding"] = "base64",
            ["webhookSignaturePrefix"] = "v1=",
            ["webhookSignedPayloadStrategy"] = "TimestampDotBody",
            ["webhookSignedPayloadDelimiter"] = ".",
            ["webhookRequireTimestamp"] = true,
        };

        // Act
        var spec = WebhookSignatureSpecResolver.Resolve(inputs, customSchemes: null);

        // Assert
        Assert.NotNull(spec);
        Assert.Equal("X-MyHook-Signature", spec!.HeaderName);
        Assert.Equal(HmacAlgorithm.Sha512, spec.Algorithm);
        Assert.Equal(SignatureEncoding.Base64, spec.Encoding);
        Assert.Equal("v1=", spec.Prefix);
        Assert.Equal(SignedPayloadStrategy.TimestampDotBody, spec.SignedPayloadStrategy);
        Assert.True(spec.RequireTimestamp);
    }
}
