using System.Collections.Concurrent;
using System.Diagnostics;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.InMemory;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;

namespace FlowOrchestrator.Hangfire.Tests;

/// <summary>One PostgreSQL container shared by the Hangfire.PostgreSql tests of a class; each test uses its own schema.</summary>
public sealed class HangfirePostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    /// <summary>Connection string of the running container.</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        // Transient Docker failures (image-pull race, reaper start contention) when TFMs start in parallel.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _container.StartAsync();
                return;
            }
            catch (Exception) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// What a hard-killed Hangfire worker leaves behind on Hangfire.PostgreSql, and how long a surviving
/// server takes to run the job it was holding.
/// </summary>
/// <remarks>
/// Hangfire.PostgreSql implements distributed locks as rows in its <c>lock</c> table. Nothing ties a row
/// to the session that wrote it, so a process killed while holding one leaves the row behind until it is
/// older than <see cref="PostgreSqlStorageOptions.DistributedLockTimeout"/> (10 min by default). The
/// worker takes the job's <c>job:{id}:state-lock</c> for every state change (Enqueued → Processing on
/// fetch, Processing → Succeeded on completion), so a kill inside either change orphans it. The job is
/// re-fetched once <see cref="PostgreSqlStorageOptions.InvisibilityTimeout"/> has passed, but the
/// re-fetching worker then waits on the orphaned row: recovery takes the longer of the two timeouts.
/// Hangfire.SqlServer takes <c>sp_getapplock</c> on the session instead, which the server releases the
/// moment the killed process's connection drops.
/// </remarks>
// AddHangfire and the servers here touch process-wide Hangfire state (JobStorage.Current, GlobalJobFilters).
[Collection(nameof(HangfireIntegrationGlobalFiltersCollection))]
public sealed class HangfirePostgreSqlCrashRecoveryTests : IClassFixture<HangfirePostgreSqlFixture>, IDisposable
{
    private static readonly TimeSpan Invisibility = TimeSpan.FromSeconds(3);

    private readonly HangfirePostgreSqlFixture _fixture;
    private readonly List<BackgroundJobServer> _servers = [];

    // A killed process runs no Dispose, so the test keeps what it "killed" reachable and never disposes it.
    private readonly List<object> _abandoned = [];

    public HangfirePostgreSqlCrashRecoveryTests(HangfirePostgreSqlFixture fixture) => _fixture = fixture;

    public void Dispose()
    {
        foreach (var server in _servers)
        {
            server.Dispose();
        }

        // AddHangfire points Hangfire's static log provider and job activator at the test's
        // ServiceProvider, which is disposed by then; reset both so later Hangfire objects do not use it.
        global::Hangfire.Logging.LogProvider.SetCurrentLogProvider(null);
        JobActivator.Current = new JobActivator();
    }

    [Fact]
    public async Task KilledMidStateChange_LockTimeoutNotAboveInvisibility_SurvivorRunsTheJob()
    {
        // Arrange — the configuration FlowOrchestrator recommends: lock timeout no longer than the
        // invisibility timeout, so the orphaned row has expired by the time the job is visible again.
        var schema = NewSchema();
        var storage = CreateStorage(schema, lockTimeout: Invisibility);
        var token = Guid.NewGuid().ToString("N");
        var ran = ProbeJob.Expect(token);
        var jobId = new BackgroundJobClient(storage).Enqueue<ProbeJob>(j => j.Run(token));
        await KillWorkerInsideStateChangeAsync(schema, jobId, lockTimeout: Invisibility);

        // Act
        StartServer(storage);

        // Assert
        await ran.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(0, await CountStateLocksAsync(schema, jobId));
    }

    [Fact]
    public async Task KilledMidStateChange_LockTimeoutAboveInvisibility_JobWaitsForTheLockTimeout()
    {
        // Arrange — the lock timeout outlives the invisibility timeout: the job is re-fetched after 3 s
        // but cannot change state until the orphaned lock row is 15 s old.
        var lockTimeout = TimeSpan.FromSeconds(15);
        var schema = NewSchema();
        var storage = CreateStorage(schema, lockTimeout);
        var token = Guid.NewGuid().ToString("N");
        var ran = ProbeJob.Expect(token);
        var jobId = new BackgroundJobClient(storage).Enqueue<ProbeJob>(j => j.Run(token));
        var sinceKill = Stopwatch.StartNew();
        await KillWorkerInsideStateChangeAsync(schema, jobId, lockTimeout);
        var orphanedLocks = await CountStateLocksAsync(schema, jobId);

        // Act
        StartServer(storage);
        await ran.WaitAsync(TimeSpan.FromSeconds(90));

        // Assert — a lower bound only: the row is deleted no earlier than lockTimeout after it was
        // written, and the stopwatch started before it was written. 1 s covers clock granularity.
        Assert.Equal(1, orphanedLocks);
        Assert.True(sinceKill.Elapsed >= lockTimeout - TimeSpan.FromSeconds(1),
            $"Job ran {sinceKill.Elapsed.TotalSeconds:0.0} s after the kill; the orphaned state lock should have held it for {lockTimeout.TotalSeconds:0} s.");
    }

    [Theory]
    [InlineData(15, true)]
    [InlineData(3, false)]
    public async Task UseHangfire_OnHangfirePostgreSql_WarnsOnlyWhenTheLockTimeoutExceedsInvisibility(int lockTimeoutSeconds, bool expectWarning)
    {
        // Arrange — wired the way an application does it, so the check reads the real package's options
        // through the JobStorage that AddHangfire registers.
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs));
        var options = new PostgreSqlStorageOptions
        {
            SchemaName = NewSchema(),
            InvisibilityTimeout = Invisibility,
            DistributedLockTimeout = TimeSpan.FromSeconds(lockTimeoutSeconds),
        };
        services.AddHangfire(c => c.UsePostgreSqlStorage(o => o.UseNpgsqlConnection(_fixture.ConnectionString), options));
        services.AddFlowOrchestrator(o =>
        {
            o.UseInMemory();
            o.UseHangfire();
        });
        await using var provider = services.BuildServiceProvider();
        var check = provider.GetServices<IHostedService>().OfType<HangfirePostgreSqlLockTimeoutCheck>().Single();

        // Act
        await check.StartAsync(CancellationToken.None);

        // Assert
        var warnings = logs.Entries.Where(e => e.EventId.Id == 3160).ToList();
        Assert.Equal(expectWarning ? 1 : 0, warnings.Count);
    }

    /// <summary>
    /// Plays a worker process that fetched <paramref name="jobId"/> and took its state lock (the first thing
    /// <c>BackgroundJobStateChanger</c> does) before being killed: nothing is disposed, and every PostgreSQL
    /// session it opened is terminated, so the only cleanup left is whatever the database does on its own.
    /// </summary>
    private async Task KillWorkerInsideStateChangeAsync(string schema, string jobId, TimeSpan lockTimeout)
    {
        var applicationName = "fo-killed-" + Guid.NewGuid().ToString("N")[..12];
        var killedConnectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            ApplicationName = applicationName,
        }.ConnectionString;
        var killedStorage = CreateStorage(schema, lockTimeout, killedConnectionString, prepareSchema: false);
        var connection = killedStorage.GetConnection();
        var fetched = connection.FetchNextJob(["default"], CancellationToken.None);
        Assert.Equal(jobId, fetched.JobId);
        var stateLock = connection.AcquireDistributedJobLock(jobId, TimeSpan.FromSeconds(10));
        _abandoned.AddRange([killedStorage, connection, fetched, stateLock]);

        await using var admin = new NpgsqlConnection(_fixture.ConnectionString);
        await admin.OpenAsync();
        await using var terminate = new NpgsqlCommand(
            "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name = @app", admin);
        terminate.Parameters.AddWithValue("app", applicationName);
        var terminated = (long)(await terminate.ExecuteScalarAsync())!;
        Assert.True(terminated > 0, "The killed worker had no PostgreSQL session to terminate.");
    }

    private async Task<long> CountStateLocksAsync(string schema, string jobId)
    {
        await using var admin = new NpgsqlConnection(_fixture.ConnectionString);
        await admin.OpenAsync();
        await using var count = new NpgsqlCommand($"SELECT count(*) FROM \"{schema}\".\"lock\" WHERE \"resource\" = @resource", admin);
        count.Parameters.AddWithValue("resource", $"{schema}:job:{jobId}:state-lock");
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private PostgreSqlStorage CreateStorage(string schema, TimeSpan lockTimeout, string? connectionString = null, bool prepareSchema = true)
    {
        var options = new PostgreSqlStorageOptions
        {
            SchemaName = schema,
            PrepareSchemaIfNecessary = prepareSchema,
            InvisibilityTimeout = Invisibility,
            DistributedLockTimeout = lockTimeout,
            QueuePollInterval = TimeSpan.FromMilliseconds(200),
        };
        return new PostgreSqlStorage(new NpgsqlConnectionFactory(connectionString ?? _fixture.ConnectionString, options), options);
    }

    private void StartServer(JobStorage storage)
    {
        _servers.Add(new BackgroundJobServer(
            new BackgroundJobServerOptions
            {
                WorkerCount = 2,
                ServerName = "survivor-" + Guid.NewGuid().ToString("N")[..8],
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                // Explicit: AddHangfire in another test replaces the static JobActivator.Current with one
                // bound to a ServiceProvider that test has already disposed.
                Activator = new JobActivator(),
            },
            storage));
    }

    private static string NewSchema() => "hf_" + Guid.NewGuid().ToString("N")[..12];

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception)));
        }
    }

    /// <summary>Job that completes the <see cref="TaskCompletionSource"/> registered for its token.</summary>
    public sealed class ProbeJob
    {
        private static readonly ConcurrentDictionary<string, TaskCompletionSource> Runs = new(StringComparer.Ordinal);

        /// <summary>Registers <paramref name="token"/> and returns the task its run completes.</summary>
        public static Task Expect(string token)
            => Runs.GetOrAdd(token, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        /// <summary>Runs the job for <paramref name="token"/>.</summary>
        public void Run(string token)
        {
            if (Runs.TryGetValue(token, out var tcs))
            {
                tcs.TrySetResult();
            }
        }
    }
}
