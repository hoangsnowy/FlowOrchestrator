using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Edge cases for the RFC 7231 §5.3.4 q-value parser used by the dashboard's
/// content-encoding negotiation. The parser itself is private; these tests
/// exercise it end-to-end through the <c>GET /flows</c> endpoint by sending
/// crafted <c>Accept-Encoding</c> headers and asserting the resulting
/// <c>Content-Encoding</c> response header.
/// </summary>
public sealed class HasEncodingParserTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;

    public HasEncodingParserTests()
    {
        _client = _server.CreateClient();
        _server.FlowStore.GetAllAsync().Returns(Array.Empty<FlowDefinitionRecord>());
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task ExplicitQ1_OnBrotli_SelectsBrotli()
    {
        // Arrange
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br;q=1.0");

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal("br", response.Content.Headers.ContentEncoding.SingleOrDefault());
    }

    [Fact]
    public async Task ExplicitQZeroPointZero_OnGzip_FallsBackToUncompressed_WhenBrotliAlsoQZero()
    {
        // Arrange — both supported codings disabled via q=0.0; nothing to serve compressed.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br;q=0.0, gzip;q=0.0");

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task NegativeQValue_IsTreatedAsZero_AndExcludesCoding()
    {
        // Arrange — RFC 7231 says q-values are 0..1; out-of-range should not
        // accidentally enable a coding. Our parser treats q <= 0 as disabled.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br;q=-0.5, gzip");

        // Act
        using var response = await _client.SendAsync(request);

        // Assert — br is disabled, gzip is allowed.
        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.SingleOrDefault());
    }

    [Fact]
    public async Task WildcardEncoding_IsNotMistakenForKnownCoding()
    {
        // Arrange — "*" is RFC 7231's wildcard meaning "anything else not
        // listed". Our parser deliberately does NOT auto-enable br/gzip on a
        // bare "*" because mismatched semantics would surprise clients that
        // explicitly enumerate codings they want.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "*");

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task DuplicateCoding_FirstOccurrenceWins()
    {
        // Arrange — the parser walks the comma list left-to-right and returns
        // on first match. A subsequent "br;q=0" entry cannot retroactively
        // disable a "br" already accepted earlier.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br, br;q=0");

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal("br", response.Content.Headers.ContentEncoding.SingleOrDefault());
    }

    [Fact]
    public async Task WhitespaceAroundCommaAndSemicolon_IsTolerated()
    {
        // Arrange — RFC 7231 allows OWS (optional whitespace) around commas
        // and inside parameter lists. Our parser must trim both.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "  gzip  ;  q=1.0  ,  br  ");

        // Act
        using var response = await _client.SendAsync(request);

        // Assert — br comes second but is preferred by the server (br > gzip
        // in our selection order regardless of list position).
        Assert.Equal("br", response.Content.Headers.ContentEncoding.SingleOrDefault());
    }
}
