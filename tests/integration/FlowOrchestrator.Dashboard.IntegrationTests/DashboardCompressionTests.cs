using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Verifies that <c>GET /flows</c> serves a pre-compressed response matching
/// the client's <c>Accept-Encoding</c> header. The dashboard root page is
/// rendered once at startup and cached in three forms — raw UTF-8, Brotli,
/// and Gzip — so each request avoids per-request CPU on serialization or
/// compression.
/// </summary>
public sealed class DashboardCompressionTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;

    public DashboardCompressionTests()
    {
        // Disable HttpClient's automatic decompression so we observe the raw
        // bytes and headers the dashboard actually emits.
        _client = _server.CreateClient();
        _server.FlowStore.GetAllAsync().Returns(Array.Empty<FlowDefinitionRecord>());
    }

    public void Dispose() => _server.Dispose();

    // ── Encoding selection ────────────────────────────────────────────────────

    [Fact]
    public async Task AcceptingBrAndGzip_PrefersBrotli()
    {
        // Arrange
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("br", response.Content.Headers.ContentEncoding.SingleOrDefault());
    }

    [Fact]
    public async Task AcceptingGzipOnly_ReturnsGzip()
    {
        // Arrange
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.SingleOrDefault());
    }

    [Fact]
    public async Task NoAcceptEncoding_ReturnsUncompressed()
    {
        // Arrange
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows");

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task UnknownAcceptEncoding_FallsBackToUncompressed()
    {
        // Arrange — "deflate" is not implemented; request a coding the server doesn't offer.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task BrotliDisabledViaQZero_FallsBackToGzip()
    {
        // Arrange — "br;q=0" explicitly opts out of Brotli even when the server supports it.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br;q=0, gzip");

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.SingleOrDefault());
    }

    // ── Vary header ───────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryResponse_AdvertisesVaryAcceptEncoding()
    {
        // Arrange
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows");

        // Act
        using var response = await _client.SendAsync(request);

        // Assert — Vary lets caches key the variant correctly.
        Assert.Contains(response.Headers.Vary, v => string.Equals(v, "Accept-Encoding", StringComparison.OrdinalIgnoreCase));
    }

    // ── Round-trip equivalence: compressed bytes decode to the original page ──

    [Fact]
    public async Task BrotliResponse_DecodesToSameContentAsUncompressed()
    {
        // Arrange
        var raw = await GetRawHtmlAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));

        // Act
        using var response = await _client.SendAsync(request);
        var decoded = await DecodeBrotliAsync(response);

        // Assert
        Assert.Equal(raw, decoded);
    }

    [Fact]
    public async Task GzipResponse_DecodesToSameContentAsUncompressed()
    {
        // Arrange
        var raw = await GetRawHtmlAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        // Act
        using var response = await _client.SendAsync(request);
        var decoded = await DecodeGzipAsync(response);

        // Assert
        Assert.Equal(raw, decoded);
    }

    // ── Compression actually shrinks the payload ──────────────────────────────

    [Fact]
    public async Task BrotliPayload_IsAtLeastFourTimesSmallerThanRaw()
    {
        // Arrange
        var raw = await GetRawHtmlAsync();
        var rawSize = Encoding.UTF8.GetByteCount(raw);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));

        // Act
        using var response = await _client.SendAsync(request);
        var compressedBytes = await response.Content.ReadAsByteArrayAsync();

        // Assert — typical ratio on the dashboard payload is ~7-8x; we assert
        // a conservative 4x lower bound to keep the test resilient against
        // future content additions.
        Assert.True(
            compressedBytes.Length * 4 < rawSize,
            $"Brotli output ({compressedBytes.Length} bytes) should be < 1/4 of raw ({rawSize} bytes).");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> GetRawHtmlAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows");
        using var response = await _client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<string> DecodeBrotliAsync(HttpResponseMessage response)
    {
        using var compressed = await response.Content.ReadAsStreamAsync();
        using var decompressor = new BrotliStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(decompressor, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task<string> DecodeGzipAsync(HttpResponseMessage response)
    {
        using var compressed = await response.Content.ReadAsStreamAsync();
        using var decompressor = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(decompressor, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    // ── JSON endpoint compression (D1 — extends Accept-Encoding to /api/*) ────

    [Fact]
    public async Task ApiFlows_WithBrotli_ReturnsBrotliEncoding()
    {
        // Arrange
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows/api/flows");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("br", response.Content.Headers.ContentEncoding.SingleOrDefault());
        Assert.Contains(response.Headers.Vary, v => string.Equals(v, "Accept-Encoding", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ApiFlows_WithGzipOnly_ReturnsGzipEncoding()
    {
        // Arrange
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows/api/flows");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.SingleOrDefault());
    }

    [Fact]
    public async Task ApiFlows_WithoutAcceptEncoding_ReturnsUncompressed()
    {
        // Arrange
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows/api/flows");

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Empty(response.Content.Headers.ContentEncoding);
        // Vary is still emitted so a downstream cache that DOES see varying
        // Accept-Encoding from other clients keys correctly.
        Assert.Contains(response.Headers.Vary, v => string.Equals(v, "Accept-Encoding", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ApiRuns_BrotliPayload_IsSignificantlySmallerThanRaw()
    {
        // Arrange — seed a realistic run-list payload (50 records).
        var runs = Enumerable.Range(0, 50).Select(i => new FlowRunRecord
        {
            Id = Guid.NewGuid(),
            FlowId = Guid.NewGuid(),
            FlowName = $"BenchmarkFlow_{i}",
            Status = i % 3 == 0 ? "Failed" : "Succeeded",
            TriggerKey = "manual",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-i).AddSeconds(30)
        }).ToArray();
        _server.FlowRunStore.GetRunsAsync(Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(runs);

        using var rawRequest = new HttpRequestMessage(HttpMethod.Get, "/flows/api/runs");
        using var rawResponse = await _client.SendAsync(rawRequest);
        var rawSize = (await rawResponse.Content.ReadAsByteArrayAsync()).Length;

        using var brRequest = new HttpRequestMessage(HttpMethod.Get, "/flows/api/runs");
        brRequest.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        using var brResponse = await _client.SendAsync(brRequest);
        var brSize = (await brResponse.Content.ReadAsByteArrayAsync()).Length;

        // Act — done above.

        // Assert — Brotli at CompressionLevel.Fastest typically produces
        // ~70% reduction on JSON payloads. Conservative 3× lower bound to
        // keep the test stable as the payload shape evolves.
        Assert.True(
            brSize * 3 < rawSize,
            $"Brotli output ({brSize} bytes) should be < 1/3 of raw ({rawSize} bytes).");
    }

    [Fact]
    public async Task ApiFlows_BrotliAndGzipDecompressToSameJson()
    {
        // Arrange — fetch raw JSON once for the baseline.
        using var rawRequest = new HttpRequestMessage(HttpMethod.Get, "/flows/api/flows");
        using var rawResponse = await _client.SendAsync(rawRequest);
        var rawJson = await rawResponse.Content.ReadAsStringAsync();

        using var brRequest = new HttpRequestMessage(HttpMethod.Get, "/flows/api/flows");
        brRequest.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        using var brResponse = await _client.SendAsync(brRequest);
        var brJson = await DecodeBrotliAsync(brResponse);

        using var gzRequest = new HttpRequestMessage(HttpMethod.Get, "/flows/api/flows");
        gzRequest.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        using var gzResponse = await _client.SendAsync(gzRequest);
        var gzJson = await DecodeGzipAsync(gzResponse);

        // Act — already done in arrange.

        // Assert — every transport variant decodes to the same JSON string.
        Assert.Equal(rawJson, brJson);
        Assert.Equal(rawJson, gzJson);
    }
}
