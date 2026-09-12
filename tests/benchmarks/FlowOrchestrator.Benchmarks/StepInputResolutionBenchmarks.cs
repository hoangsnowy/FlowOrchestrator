using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FlowOrchestrator.Core.Execution.Internal;

namespace FlowOrchestrator.Benchmarks;

/// <summary>
/// Measures <see cref="InputResolutionPipeline.Resolve"/> — pass 1 of
/// <c>DefaultStepExecutor.ExecuteAsync</c>, run once per step execution against the step's whole
/// input dictionary.
/// </summary>
/// <remarks>
/// <para>
/// The existing <c>expression-resolver-2026-05-02</c> baseline measures a <b>single</b>
/// <c>@triggerBody().orderId</c> resolution (546 ns / 800 B) and attributes the cost to
/// <c>JsonSerializer.SerializeToElement</c>. That measurement uses a POCO trigger payload. In
/// production the payload is whatever <c>IOutputsRepository.GetTriggerDataAsync</c> returns, which
/// for every first-party store is a <see cref="JsonElement"/> — so the resolver takes
/// <c>ExpressionPathHelper.ToJsonElement</c>'s <c>element.Clone()</c> branch instead, which copies
/// and re-parses the payload's raw bytes.
/// </para>
/// <para>
/// Either branch runs <b>per expression</b>, not per step: a step with N trigger expressions
/// converts the same payload N times. These benchmarks sweep N (1 / 4 / 12 inputs) against a fixed
/// payload and sweep payload size against a fixed input count, so the two multiplicands are
/// separable. A flat line across <c>ExpressionCount</c> would mean the conversion is already
/// hoisted; a linear one means it is not.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class StepInputResolutionBenchmarks
{
    /// <summary>Number of <c>@triggerBody()</c> expressions in the step's input dictionary.</summary>
    [Params(1, 4, 12)]
    public int ExpressionCount { get; set; }

    /// <summary>Number of order-line objects in the trigger payload, driving its byte size.</summary>
    [Params(1, 50)]
    public int PayloadLines { get; set; }

    private static readonly JsonSerializerOptions _webOptions = new(JsonSerializerDefaults.Web);

    private object _jsonElementPayload = null!;
    private Dictionary<string, object?> _expressionInputs = null!;
    private Dictionary<string, object?> _literalInputs = null!;
    private Dictionary<string, object?> _deepPathInputs = null!;

    /// <summary>Builds the trigger payload and the three input-dictionary shapes under test.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var lines = new List<object>(PayloadLines);
        for (var i = 0; i < PayloadLines; i++)
        {
            lines.Add(new { sku = $"SKU-{i:D5}", qty = i + 1, unitPrice = 19.99m + i, note = "standard fulfilment" });
        }

        var payload = new
        {
            orderId = "ORD-9001",
            customerId = "CUST-4242",
            channel = "web",
            total = 1234.56m,
            customer = new { id = "CUST-4242", name = "Ada Lovelace", address = new { city = "London", postcode = "EC1A" } },
            lines
        };

        // Round-trip through JSON: this is what GetTriggerDataAsync hands the resolver on the
        // SQL Server, PostgreSQL and InMemory stores alike.
        _jsonElementPayload = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(payload, _webOptions), _webOptions);

        _expressionInputs = new Dictionary<string, object?>(StringComparer.Ordinal);
        _deepPathInputs = new Dictionary<string, object?>(StringComparer.Ordinal);
        _literalInputs = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < ExpressionCount; i++)
        {
            _expressionInputs[$"input_{i}"] = "@triggerBody().orderId";
            _deepPathInputs[$"input_{i}"] = "@triggerBody().customer.address.city";
            _literalInputs[$"input_{i}"] = "ORD-9001";
        }
    }

    /// <summary>
    /// The common case the pipeline already fast-paths: no value can be an expression, so the
    /// original dictionary is returned untouched. Included as the floor the other cases are
    /// measured against.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Resolve — all literal inputs (fast path)")]
    public int ResolveLiterals() =>
        InputResolutionPipeline.Resolve(_literalInputs, _jsonElementPayload, null).Count;

    /// <summary>
    /// N single-segment <c>@triggerBody().orderId</c> expressions against a JsonElement payload.
    /// Scaling with <c>ExpressionCount</c> measures the repeated payload conversion; scaling with
    /// <c>PayloadLines</c> measures how much of that conversion is proportional to payload size.
    /// </summary>
    [Benchmark(Description = "Resolve — N × @triggerBody().orderId (JsonElement payload)")]
    public int ResolveShallowExpressions() =>
        InputResolutionPipeline.Resolve(_expressionInputs, _jsonElementPayload, null).Count;

    /// <summary>
    /// N three-segment <c>@triggerBody().customer.address.city</c> expressions. The delta against
    /// <see cref="ResolveShallowExpressions"/> isolates the per-segment path-walk cost —
    /// <c>ExpressionPathHelper.TryResolvePath</c> does two <c>string.Replace</c> passes plus a
    /// <c>string.Split</c> on every call.
    /// </summary>
    [Benchmark(Description = "Resolve — N × @triggerBody().customer.address.city (3 segments)")]
    public int ResolveDeepPathExpressions() =>
        InputResolutionPipeline.Resolve(_deepPathInputs, _jsonElementPayload, null).Count;
}
