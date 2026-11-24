using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FlowOrchestrator.Dashboard.Security;

namespace FlowOrchestrator.Benchmarks;

/// <summary>
/// Cost of the viewer-side secret masking in <see cref="ManifestSecretRedactor"/>, which runs on every
/// dashboard poll a read-only caller makes (run list headers, run detail payloads, webhook log headers).
/// </summary>
/// <remarks>
/// Names are normalised before matching (separators dropped), so the cheap text pre-check also has to search
/// the normalised text whenever the payload contains a separator — which nearly every payload does (GUIDs,
/// dates, decimals). These cases measure that extra pass and the per-name check.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class SecretRedactionBenchmarks
{
    private string _payloadWithSeparators = null!;
    private string _payloadWithoutSeparators = null!;
    private Dictionary<string, string> _headers = null!;

    /// <summary>Builds ~1 KB step outputs with no secret-looking name and a typical webhook header map.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var items = string.Join(",", Enumerable.Range(0, 12).Select(i =>
            $"{{\"lineId\":\"{Guid.NewGuid()}\",\"placedAt\":\"2026-11-24T10:{i:00}:00Z\",\"total\":{i}.5}}"));
        _payloadWithSeparators = $"{{\"orderId\":\"{Guid.NewGuid()}\",\"items\":[{items}]}}";

        var plain = string.Join(",", Enumerable.Range(0, 40).Select(i => $"{{\"lineId\":{i},\"qty\":{i % 7}}}"));
        _payloadWithoutSeparators = $"{{\"orderId\":4711,\"items\":[{plain}]}}";

        _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json",
            ["User-Agent"] = "GitHub-Hookshot/abc",
            ["X-GitHub-Delivery"] = Guid.NewGuid().ToString(),
            ["X-GitHub-Event"] = "push",
            ["X-Hub-Signature-256"] = "sha256=00",
            ["X-Request-Id"] = Guid.NewGuid().ToString(),
            ["X-Api-Key"] = "k",
            ["Accept"] = "*/*",
            ["Content-Length"] = "1024",
            ["Host"] = "flows.example",
        };
    }

    /// <summary>Pre-check + pass-through of a payload with separators and no secret-looking name.</summary>
    [Benchmark(Description = "RedactJson, ~1 KB, separators, no secret name")]
    public string? RedactJsonWithSeparators() => ManifestSecretRedactor.RedactJson(_payloadWithSeparators);

    /// <summary>Pre-check + pass-through of a payload with neither separators nor a secret-looking name.</summary>
    [Benchmark(Description = "RedactJson, ~1 KB, no separators, no secret name")]
    public string? RedactJsonWithoutSeparators() => ManifestSecretRedactor.RedactJson(_payloadWithoutSeparators);

    /// <summary>Per-name check for a hyphenated credential header.</summary>
    [Benchmark(Description = "IsSecretName(\"Ocp-Apim-Subscription-Key\")")]
    public bool IsSecretNameHyphenated() => ManifestSecretRedactor.IsSecretName("Ocp-Apim-Subscription-Key");

    /// <summary>Per-name check for an ordinary name without separators.</summary>
    [Benchmark(Description = "IsSecretName(\"orderId\")")]
    public bool IsSecretNamePlain() => ManifestSecretRedactor.IsSecretName("orderId");

    /// <summary>Masking a ten-header webhook / trigger header map.</summary>
    [Benchmark(Description = "MaskHeaders, 10 headers")]
    public object? MaskHeaders() => ManifestSecretRedactor.MaskHeaders(_headers);
}
