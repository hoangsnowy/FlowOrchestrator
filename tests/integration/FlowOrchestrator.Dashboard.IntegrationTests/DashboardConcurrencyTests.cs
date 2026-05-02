using System.Net.Http.Headers;
using System.Security.Cryptography;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Verifies that the pre-compressed dashboard root response is safe to share
/// across many concurrent requests. The <c>PrecompressedDashboardPage</c> type
/// holds three byte buffers built once at startup and reused for every request;
/// any accidental mutation under concurrent traffic would manifest as
/// drifting hashes between requests in the same burst.
/// </summary>
public sealed class DashboardConcurrencyTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;

    public DashboardConcurrencyTests()
    {
        _client = _server.CreateClient();
        _server.FlowStore.GetAllAsync().Returns(Array.Empty<FlowDefinitionRecord>());
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task OneHundredParallelBrotliRequests_AllReturnByteIdenticalResponses()
    {
        // Arrange
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var tasks = Enumerable.Range(0, 100).Select(async _ =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/flows");
            req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
            await startGate.Task;
            using var resp = await _client.SendAsync(req);
            return await resp.Content.ReadAsByteArrayAsync();
        }).ToArray();
        startGate.SetResult();
        var bodies = await Task.WhenAll(tasks);

        // Assert
        var firstHash = SHA256.HashData(bodies[0]);
        foreach (var body in bodies)
        {
            Assert.Equal(firstHash, SHA256.HashData(body));
        }
    }

    [Fact]
    public async Task OneHundredParallelGzipRequests_AllReturnByteIdenticalResponses()
    {
        // Arrange
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var tasks = Enumerable.Range(0, 100).Select(async _ =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/flows");
            req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            await startGate.Task;
            using var resp = await _client.SendAsync(req);
            return await resp.Content.ReadAsByteArrayAsync();
        }).ToArray();
        startGate.SetResult();
        var bodies = await Task.WhenAll(tasks);

        // Assert
        var firstHash = SHA256.HashData(bodies[0]);
        foreach (var body in bodies)
        {
            Assert.Equal(firstHash, SHA256.HashData(body));
        }
    }

    [Fact]
    public async Task BrotliBufferUnchanged_BeforeAndAfterParallelBurst()
    {
        // Arrange — capture the byte buffer that will be served, run a heavy
        // concurrent burst, then capture again. A mutation of the shared
        // pre-compressed array would surface here.
        using var probeRequest = new HttpRequestMessage(HttpMethod.Get, "/flows");
        probeRequest.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        using var probeResponseBefore = await _client.SendAsync(probeRequest);
        var bytesBefore = await probeResponseBefore.Content.ReadAsByteArrayAsync();
        var hashBefore = SHA256.HashData(bytesBefore);

        // Act
        var burst = Enumerable.Range(0, 100).Select(async _ =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/flows");
            req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
            using var resp = await _client.SendAsync(req);
            await resp.Content.ReadAsByteArrayAsync();
        }).ToArray();
        await Task.WhenAll(burst);

        using var probeRequestAfter = new HttpRequestMessage(HttpMethod.Get, "/flows");
        probeRequestAfter.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        using var probeResponseAfter = await _client.SendAsync(probeRequestAfter);
        var bytesAfter = await probeResponseAfter.Content.ReadAsByteArrayAsync();
        var hashAfter = SHA256.HashData(bytesAfter);

        // Assert
        Assert.Equal(hashBefore, hashAfter);
    }
}
