using System.Diagnostics;
using FlowOrchestrator.Core.Observability;

namespace FlowOrchestrator.Core.Tests.Observability;

/// <summary>
/// Unit tests for <see cref="InboundTraceContext"/> — the helper that lets the Dashboard's
/// webhook and signal endpoints stitch flow-runs onto the caller's distributed trace.
/// </summary>
public sealed class InboundTraceContextTests
{
    private const string ValidTraceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    [Fact]
    public void TryParse_ReturnsTrue_AndYieldsRemoteContext_ForValidTraceparent()
    {
        // Arrange + Act
        var ok = InboundTraceContext.TryParse(ValidTraceparent, tracestate: null, out var context);

        // Assert
        Assert.True(ok);
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", context.TraceId.ToString());
        Assert.Equal("b7ad6b7169203331", context.SpanId.ToString());
        Assert.True(context.IsRemote);
    }

    [Fact]
    public void TryParse_ReturnsFalse_WhenTraceparentMissing()
    {
        // Arrange + Act
        var ok = InboundTraceContext.TryParse(traceparent: null, tracestate: null, out var context);

        // Assert
        Assert.False(ok);
        Assert.Equal(default, context);
    }

    [Fact]
    public void TryParse_ReturnsFalse_ForMalformedTraceparent()
    {
        // Arrange + Act
        var ok = InboundTraceContext.TryParse("not-a-traceparent", tracestate: null, out var context);

        // Assert
        Assert.False(ok);
        Assert.Equal(default, context);
    }

    [Fact]
    public void StartActivity_CreatesChildOfInboundContext_WhenHeaderPresent()
    {
        // Arrange
        using var source = new ActivitySource(nameof(InboundTraceContextTests));
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == nameof(InboundTraceContextTests),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = captured.Add
        };
        ActivitySource.AddActivityListener(listener);

        // Act
        using (InboundTraceContext.StartActivity(source, "flow.webhook.receive", ActivityKind.Server, ValidTraceparent, null))
        {
        }

        // Assert
        var activity = Assert.Single(captured);
        Assert.Equal("flow.webhook.receive", activity.OperationName);
        Assert.Equal(ActivityKind.Server, activity.Kind);
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", activity.TraceId.ToString());
        Assert.Equal("b7ad6b7169203331", activity.ParentSpanId.ToString());
    }

    [Fact]
    public void StartActivity_CreatesRoot_WhenNoInboundHeader()
    {
        // Arrange
        using var source = new ActivitySource(nameof(InboundTraceContextTests) + "_Root");
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == nameof(InboundTraceContextTests) + "_Root",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = captured.Add
        };
        ActivitySource.AddActivityListener(listener);

        // Act
        using (InboundTraceContext.StartActivity(source, "flow.webhook.receive", ActivityKind.Server, null, null))
        {
        }

        // Assert
        var activity = Assert.Single(captured);
        Assert.Equal("flow.webhook.receive", activity.OperationName);
        // A root activity has a zero ParentSpanId.
        Assert.Equal(default, activity.ParentSpanId);
    }
}
