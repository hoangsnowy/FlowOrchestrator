using FlowOrchestrator.Core.Observability;

namespace FlowOrchestrator.Core.Tests.Observability;

/// <summary>
/// Pins the <c>step_key</c> metric tag value: the manifest (template) key, so ForEach iterations share one
/// time series instead of creating one per index.
/// </summary>
public sealed class MetricStepKeyTests
{
    [Theory]
    [InlineData("fetch", "fetch")]
    [InlineData("process_items.iterate", "process_items.iterate")]
    [InlineData("process_items.0.iterate", "process_items.iterate")]
    [InlineData("process_items.417.iterate", "process_items.iterate")]
    [InlineData("outer.3.inner.12.child", "outer.inner.child")]
    [InlineData("loop.5", "loop")]
    [InlineData("42", "42")]
    public void For_strips_loop_index_segments(string runtimeKey, string expected)
    {
        // Arrange

        // Act
        var tag = MetricStepKey.For(runtimeKey);

        // Assert
        Assert.Equal(expected, tag);
    }

    [Fact]
    public void For_returns_the_same_instance_for_every_iteration_of_one_template()
    {
        // Arrange
        var first = MetricStepKey.For("metric_key_test_loop.0.child");

        // Act
        var later = MetricStepKey.For("metric_key_test_loop.999.child");

        // Assert
        Assert.Same(first, later);
    }

    [Fact]
    public void For_returns_a_key_without_numeric_segments_unchanged_and_without_copying()
    {
        // Arrange
        var key = string.Concat("plain", ".", "child");

        // Act
        var tag = MetricStepKey.For(key);

        // Assert
        Assert.Same(key, tag);
    }

    [Fact]
    public void For_handles_keys_longer_than_the_stack_buffer()
    {
        // Arrange
        var loop = new string('l', 300);
        var runtimeKey = loop + ".7.child";

        // Act
        var tag = MetricStepKey.For(runtimeKey);

        // Assert
        Assert.Equal(loop + ".child", tag);
    }
}
