namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Pins <see cref="AcceptEncoding.Has"/>: the RFC 9110 q=0 disable form, case-insensitivity and odd spacing — and
/// that it allocates nothing, because it runs on every dashboard response including the auto-refresh polls.
/// </summary>
public sealed class AcceptEncodingTests
{
    [Theory]
    [InlineData(null, "br", false)]
    [InlineData("", "br", false)]
    [InlineData("gzip, deflate, br", "br", true)]
    [InlineData("gzip, deflate, br", "gzip", true)]
    [InlineData("gzip, deflate", "br", false)]
    [InlineData("BR", "br", true)]
    [InlineData("  br  ", "br", true)]
    [InlineData("br;q=0", "br", false)]
    [InlineData("br; q=0.0", "br", false)]
    [InlineData("br;q=0, gzip", "gzip", true)]
    [InlineData("br;q=0.5", "br", true)]
    [InlineData("br;q=abc", "br", true)]
    [InlineData("br;level=5", "br", true)]
    [InlineData("br;level=5;q=0", "br", false)]
    [InlineData(",,br,,", "br", true)]
    [InlineData("brotli", "br", false)]
    [InlineData("x-gzip", "gzip", false)]
    [InlineData("*", "br", false)]
    public void Has_reads_the_header_like_the_previous_split_based_parser(string? header, string token, bool expected)
    {
        // Arrange — cases cover every branch of the parser it replaces.

        // Act
        var actual = AcceptEncoding.Has(header, token);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Has_allocates_nothing()
    {
        // Arrange — a realistic browser header with a parameter list; warm up so JIT/tiering does not count.
        const string header = "gzip, deflate, br;q=1.0, zstd;q=0";
        for (var i = 0; i < 100; i++)
        {
            AcceptEncoding.Has(header, "br");
            AcceptEncoding.Has(header, "zstd");
        }

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        var br = AcceptEncoding.Has(header, "br");
        var zstd = AcceptEncoding.Has(header, "zstd");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.True(br);
        Assert.False(zstd);
        Assert.Equal(0, allocated);
    }
}
