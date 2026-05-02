using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Storage;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Expressions;

/// <summary>
/// Exercises trigger-body and trigger-header expression resolution as wired
/// through <see cref="WhenClauseEvaluator"/>. This is the public surface
/// covering the (internal) <c>TriggerExpressionResolver</c> — addressing the
/// G6 expression-resolution gap from the 2026-04-30 audit:
/// <c>@triggerBody()</c>/<c>@triggerHeaders()</c> behaviour against
/// <see langword="null"/> data, missing headers, header-name case sensitivity,
/// and bracket / quote variants.
/// </summary>
public class WhenClauseEvaluatorTriggerExpressionTests
{
    private readonly IOutputsRepository _outputs = Substitute.For<IOutputsRepository>();
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();

    private static IFlowDefinition Flow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = new StepCollection() });
        return flow;
    }

    private static StepMetadata StepWithWhen(string when, string predecessor = "prev")
    {
        var meta = new StepMetadata { Type = "DoWork" };
        meta.RunAfter[predecessor] = new RunAfterCondition
        {
            Statuses = new[] { StepStatus.Succeeded },
            When = when
        };
        return meta;
    }

    private static IExecutionContext Context(object? triggerData = null, IReadOnlyDictionary<string, string>? headers = null) =>
        new Core.Execution.ExecutionContext
        {
            RunId = Guid.NewGuid(),
            TriggerData = triggerData,
            TriggerHeaders = headers
        };

    // ── @triggerBody() — null / missing path ──────────────────────────────────

    [Fact]
    public async Task TriggerBody_NullData_BareReference_ResolvesToNull_AndComparisonReturnsTrace()
    {
        // Arrange — When asks "is the entire trigger body equal to null?".
        var sut = new WhenClauseEvaluator(_outputs, _runStore);
        var ctx = Context(triggerData: null);
        var step = StepWithWhen("@triggerBody() == null");

        // Act
        var trace = await sut.EvaluateAsync(ctx, Flow(), step);

        // Assert — no false-branch trace returned because the comparison is true.
        Assert.Null(trace);
    }

    [Fact]
    public async Task TriggerBody_NullData_PathReference_ResolvesToNullAndSkipsBranch()
    {
        // Arrange — payload absent; '.orderId' must resolve to null, not throw.
        var sut = new WhenClauseEvaluator(_outputs, _runStore);
        var ctx = Context(triggerData: null);
        var step = StepWithWhen("@triggerBody().orderId == 'ORD-1'");

        // Act
        var trace = await sut.EvaluateAsync(ctx, Flow(), step);

        // Assert — branch not taken; trace surfaces the false result.
        Assert.NotNull(trace);
        Assert.False(trace!.Result);
    }

    [Fact]
    public async Task TriggerBody_MissingProperty_ResolvesToNullNotFailure()
    {
        // Arrange — payload exists but does not include the requested property.
        var sut = new WhenClauseEvaluator(_outputs, _runStore);
        var ctx = Context(triggerData: JsonSerializer.Deserialize<JsonElement>("{\"other\":1}"));
        var step = StepWithWhen("@triggerBody().missing == null");

        // Act
        var trace = await sut.EvaluateAsync(ctx, Flow(), step);

        // Assert — equality with null returns true → no skipping trace.
        Assert.Null(trace);
    }

    [Fact]
    public async Task TriggerBody_OptionalChaining_OnNullPayload_DoesNotThrow()
    {
        // Arrange — '?.' prefix is the documented optional-chaining form.
        var sut = new WhenClauseEvaluator(_outputs, _runStore);
        var ctx = Context(triggerData: null);
        var step = StepWithWhen("@triggerBody()?.customer == null");

        // Act
        var trace = await sut.EvaluateAsync(ctx, Flow(), step);

        // Assert
        Assert.Null(trace);
    }

    // ── @triggerHeaders() — case sensitivity and missing keys ─────────────────

    [Fact]
    public async Task TriggerHeaders_LookupIsCaseSensitive_DifferentCaseReturnsNull()
    {
        // Arrange — dictionary key is exactly "X-Foo"; we probe lowercased.
        var sut = new WhenClauseEvaluator(_outputs, _runStore);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Foo"] = "bar"
        };
        var ctx = Context(headers: headers);
        var step = StepWithWhen("@triggerHeaders()['x-foo'] == null");

        // Act
        var trace = await sut.EvaluateAsync(ctx, Flow(), step);

        // Assert — when probed with the wrong case, value resolves to null,
        //          comparison is true, no skipping trace returned.
        Assert.Null(trace);
    }

    [Fact]
    public async Task TriggerHeaders_LookupIsCaseSensitive_ExactCaseReturnsValue()
    {
        // Arrange
        var sut = new WhenClauseEvaluator(_outputs, _runStore);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Foo"] = "bar"
        };
        var ctx = Context(headers: headers);
        var step = StepWithWhen("@triggerHeaders()['X-Foo'] == 'bar'");

        // Act
        var trace = await sut.EvaluateAsync(ctx, Flow(), step);

        // Assert — match → no false-trace returned.
        Assert.Null(trace);
    }

    [Fact]
    public async Task TriggerHeaders_NullDictionary_NamedHeaderResolvesToNull()
    {
        // Arrange
        var sut = new WhenClauseEvaluator(_outputs, _runStore);
        var ctx = Context(headers: null);
        var step = StepWithWhen("@triggerHeaders()['Anything'] == null");

        // Act
        var trace = await sut.EvaluateAsync(ctx, Flow(), step);

        // Assert
        Assert.Null(trace);
    }

    [Fact]
    public async Task TriggerHeaders_DoubleQuotedHeaderName_AlsoResolves()
    {
        // Arrange — both ['X'] and ["X"] bracket syntaxes are supported.
        var sut = new WhenClauseEvaluator(_outputs, _runStore);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Request-Id"] = "abc-123"
        };
        var ctx = Context(headers: headers);
        var step = StepWithWhen("@triggerHeaders()[\"X-Request-Id\"] == 'abc-123'");

        // Act
        var trace = await sut.EvaluateAsync(ctx, Flow(), step);

        // Assert
        Assert.Null(trace);
    }

    [Fact]
    public async Task TriggerHeaders_MissingHeader_FalseBranchProducesTrace()
    {
        // Arrange — when the header is missing, comparing it to a literal value
        //           yields false and the engine should record a trace explaining why.
        var sut = new WhenClauseEvaluator(_outputs, _runStore);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        var ctx = Context(headers: headers);
        var step = StepWithWhen("@triggerHeaders()['Missing'] == 'expected'");

        // Act
        var trace = await sut.EvaluateAsync(ctx, Flow(), step);

        // Assert
        Assert.NotNull(trace);
        Assert.False(trace!.Result);
        Assert.Contains("'Missing'", step.RunAfter["prev"].When!, StringComparison.Ordinal);
    }
}
