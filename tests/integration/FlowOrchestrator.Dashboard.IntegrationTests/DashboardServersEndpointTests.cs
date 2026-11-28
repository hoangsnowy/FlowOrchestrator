using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// <c>GET /flows/api/servers</c> (the Servers view) and the server id on run-detail attempts: the untracked state
/// for a storage backend without <see cref="IFlowServerStore"/>, the Active / Stale status against the configured
/// threshold, viewer access, and the compression contract.
/// </summary>
public sealed class DashboardServersEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Without_a_server_store_the_endpoint_reports_untracked()
    {
        // Arrange
        using var server = new DashboardTestServer();

        // Act
        using var response = await server.Client.GetAsync("/flows/api/servers");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body.RootElement.GetProperty("tracked").GetBoolean());
        Assert.Equal(0, body.RootElement.GetProperty("servers").GetArrayLength());
        Assert.Equal(45, body.RootElement.GetProperty("staleAfterSeconds").GetDouble());
    }

    [Fact]
    public async Task Lists_servers_with_an_active_or_stale_status_from_the_configured_threshold()
    {
        // Arrange
        var store = Substitute.For<IFlowServerStore>();
        store.GetServersAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            Server("host-b:2:bbbbbbbbbbbb", "host-b", Now.AddSeconds(-25)),
            Server("host-a:1:aaaaaaaaaaaa", "host-a", Now.AddSeconds(-5)),
        });
        using var server = CreateServer(store, new FlowServerOptions { HeartbeatInterval = TimeSpan.FromSeconds(10), MissedHeartbeatsBeforeStale = 2 });

        // Act
        using var response = await server.Client.GetAsync("/flows/api/servers");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Assert
        var root = body.RootElement;
        Assert.True(root.GetProperty("tracked").GetBoolean());
        Assert.Equal(20, root.GetProperty("staleAfterSeconds").GetDouble());
        var servers = root.GetProperty("servers").EnumerateArray().ToList();
        Assert.Equal(2, servers.Count);
        Assert.Equal("host-a:1:aaaaaaaaaaaa", servers[0].GetProperty("serverId").GetString());
        Assert.Equal("Active", servers[0].GetProperty("status").GetString());
        Assert.Equal(5, servers[0].GetProperty("lastSeenSeconds").GetInt64());
        Assert.Equal("Stale", servers[1].GetProperty("status").GetString());
        Assert.Equal("Hangfire", servers[1].GetProperty("runtime").GetString());
        Assert.Equal(3, servers[1].GetProperty("activeStepCount").GetInt32());
    }

    [Fact]
    public async Task A_read_only_viewer_may_list_servers()
    {
        // Arrange — production, no auth configured: anonymous callers are viewers.
        var store = Substitute.For<IFlowServerStore>();
        store.GetServersAsync(Arg.Any<CancellationToken>()).Returns(new[] { Server("h:1:x", "h", Now) });
        using var server = CreateServer(store, environment: Environments.Production);

        // Act
        using var response = await server.Client.GetAsync("/flows/api/servers");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Compressed_variants_decompress_to_the_uncompressed_bytes_and_vary_on_accept_encoding()
    {
        // Arrange
        var store = Substitute.For<IFlowServerStore>();
        store.GetServersAsync(Arg.Any<CancellationToken>()).Returns(Enumerable.Range(0, 20)
            .Select(i => Server($"host-{i}:{i}:{Guid.NewGuid():N}", $"host-{i}", Now.AddSeconds(-5.0 * i)))
            .ToArray());
        using var server = CreateServer(store);

        // Act
        using var raw = await SendAsync(server.Client, encoding: null);
        using var br = await SendAsync(server.Client, "br");
        using var gzip = await SendAsync(server.Client, "gzip");
        var rawBytes = await raw.Content.ReadAsByteArrayAsync();
        var brBytes = await DecodeAsync(br, s => new BrotliStream(s, CompressionMode.Decompress));
        var gzipBytes = await DecodeAsync(gzip, s => new GZipStream(s, CompressionMode.Decompress));

        // Assert
        Assert.Empty(raw.Content.Headers.ContentEncoding);
        Assert.Equal("br", Assert.Single(br.Content.Headers.ContentEncoding));
        Assert.Equal("gzip", Assert.Single(gzip.Content.Headers.ContentEncoding));
        Assert.Contains("Accept-Encoding", raw.Headers.Vary);
        Assert.Contains("Accept-Encoding", br.Headers.Vary);
        Assert.Equal(rawBytes, brBytes);
        Assert.Equal(rawBytes, gzipBytes);
        Assert.Contains("\"servers\"", Encoding.UTF8.GetString(rawBytes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_detail_attempts_carry_the_server_id_for_operators_and_viewers(bool operatorAccess)
    {
        // Arrange
        var runId = Guid.NewGuid();
        using var server = operatorAccess ? new DashboardTestServer() : DashboardTestServer.InEnvironment(Environments.Production);
        server.FlowRunStore.GetRunDetailAsync(runId).Returns(new FlowRunRecord
        {
            Id = runId,
            Status = "Failed",
            FlowName = "F",
            Steps =
            [
                new FlowStepRecord
                {
                    RunId = runId,
                    StepKey = "a",
                    StepType = "Work",
                    Status = "Failed",
                    Attempts =
                    [
                        new FlowStepAttemptRecord { RunId = runId, StepKey = "a", Attempt = 1, StepType = "Work", Status = "Failed", ServerId = "host-a:1:abc" },
                        new FlowStepAttemptRecord { RunId = runId, StepKey = "a", Attempt = 2, StepType = "Work", Status = "Failed", ServerId = "host-b:2:def" },
                    ],
                },
            ],
        });

        // Act
        using var response = await server.Client.GetAsync($"/flows/api/runs/{runId}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var attempts = body.RootElement.GetProperty("steps")[0].GetProperty("attempts");
        Assert.Equal("host-a:1:abc", attempts[0].GetProperty("serverId").GetString());
        Assert.Equal("host-b:2:def", attempts[1].GetProperty("serverId").GetString());
    }

    private static DashboardTestServer CreateServer(IFlowServerStore store, FlowServerOptions? options = null, string? environment = null)
        => DashboardTestServer.WithServices(services =>
        {
            services.AddFlowDashboard();
            services.AddSingleton(store);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            if (options is not null)
            {
                services.AddSingleton(options);
            }
        }, environment);

    private static FlowServerRecord Server(string id, string machine, DateTimeOffset lastHeartbeat) => new()
    {
        ServerId = id,
        MachineName = machine,
        ProcessId = 1,
        Runtime = "Hangfire",
        Version = "2.0.0",
        StartedAt = lastHeartbeat.AddHours(-1),
        LastHeartbeatAt = lastHeartbeat,
        ActiveStepCount = 3,
        FlowCount = 2,
    };

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string? encoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows/api/servers");
        if (encoding is not null)
        {
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(encoding));
        }

        return await client.SendAsync(request);
    }

    private static async Task<byte[]> DecodeAsync(HttpResponseMessage response, Func<Stream, Stream> decoder)
    {
        await using var compressed = await response.Content.ReadAsStreamAsync();
        await using var decompressor = decoder(compressed);
        using var buffer = new MemoryStream();
        await decompressor.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>A clock frozen at one instant, so statuses do not depend on when the test runs.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
