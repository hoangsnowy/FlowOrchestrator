using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Hangfire;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Hangfire.Tests
{
    // UseHangfire() installs a process-wide Hangfire job filter; share the serialized collection.
    [Collection(nameof(Telemetry.HangfireGlobalFiltersCollection))]
    public sealed class HangfirePostgreSqlLockTimeoutCheckTests
    {
        private readonly CapturingLogger _logger = new();

        [Fact]
        public void TryReadTimeouts_PostgreSqlStorage_ReadsBothTimeouts()
        {
            // Arrange
            var storage = new global::Hangfire.PostgreSql.PostgreSqlStorage(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10));

            // Act
            var read = HangfirePostgreSqlLockTimeoutCheck.TryReadTimeouts(storage, out var invisibility, out var lockTimeout);

            // Assert
            Assert.True(read);
            Assert.Equal(TimeSpan.FromSeconds(60), invisibility);
            Assert.Equal(TimeSpan.FromMinutes(10), lockTimeout);
        }

        [Fact]
        public void TryReadTimeouts_SubclassOfPostgreSqlStorage_ReadsBothTimeouts()
        {
            // Arrange
            var storage = new DerivedPostgreSqlStorage(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));

            // Act
            var read = HangfirePostgreSqlLockTimeoutCheck.TryReadTimeouts(storage, out var invisibility, out var lockTimeout);

            // Assert
            Assert.True(read);
            Assert.Equal(TimeSpan.FromMinutes(2), invisibility);
            Assert.Equal(TimeSpan.FromMinutes(5), lockTimeout);
        }

        [Fact]
        public void TryReadTimeouts_OtherStorage_ReturnsFalse()
        {
            // Arrange
            var storage = new OtherStorage();

            // Act
            var read = HangfirePostgreSqlLockTimeoutCheck.TryReadTimeouts(storage, out var invisibility, out var lockTimeout);

            // Assert
            Assert.False(read);
            Assert.Equal(TimeSpan.Zero, invisibility);
            Assert.Equal(TimeSpan.Zero, lockTimeout);
        }

        [Fact]
        public void TryReadTimeouts_OptionsMissing_ReturnsFalse()
        {
            // Arrange
            var storage = new global::Hangfire.PostgreSql.PostgreSqlStorage(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10)) { NullOptions = true };

            // Act
            var read = HangfirePostgreSqlLockTimeoutCheck.TryReadTimeouts(storage, out _, out _);

            // Assert
            Assert.False(read);
        }

        [Fact]
        public void TryReadTimeouts_OptionsGetterThrows_ReturnsFalse()
        {
            // Arrange
            var storage = new global::Hangfire.PostgreSql.PostgreSqlStorage(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10)) { ThrowFromOptions = true };

            // Act
            var read = HangfirePostgreSqlLockTimeoutCheck.TryReadTimeouts(storage, out _, out _);

            // Assert
            Assert.False(read);
        }

        [Fact]
        public void Check_LockTimeoutLongerThanInvisibility_LogsOneWarningWithBothValues()
        {
            // Arrange — the stress-harness configuration that left steps "dispatched, never claimed".
            var check = CreateCheck(new global::Hangfire.PostgreSql.PostgreSqlStorage(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10)));

            // Act
            var warned = check.Check();

            // Assert
            Assert.True(warned);
            var entry = Assert.Single(_logger.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Equal(3160, entry.EventId.Id);
            Assert.Contains("00:10:00", entry.Message, StringComparison.Ordinal);
            Assert.Contains("00:01:00", entry.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(1800, 600)] // Hangfire.PostgreSql defaults: 30 min invisibility, 10 min lock timeout.
        [InlineData(60, 60)]
        [InlineData(300, 60)]
        public void Check_LockTimeoutNotLongerThanInvisibility_DoesNotWarn(int invisibilitySeconds, int lockTimeoutSeconds)
        {
            // Arrange
            var check = CreateCheck(new global::Hangfire.PostgreSql.PostgreSqlStorage(
                TimeSpan.FromSeconds(invisibilitySeconds), TimeSpan.FromSeconds(lockTimeoutSeconds)));

            // Act
            var warned = check.Check();

            // Assert
            Assert.False(warned);
            Assert.Empty(_logger.Entries);
        }

        [Fact]
        public void Check_OtherStorage_DoesNotWarn()
        {
            // Arrange
            var check = CreateCheck(new OtherStorage());

            // Act
            var warned = check.Check();

            // Assert
            Assert.False(warned);
            Assert.Empty(_logger.Entries);
        }

        [Fact]
        public void Check_NoJobStorageRegistered_DoesNotWarn()
        {
            // Arrange
            var check = new HangfirePostgreSqlLockTimeoutCheck(new ServiceCollection().BuildServiceProvider(), _logger);

            // Act
            var warned = check.Check();

            // Assert
            Assert.False(warned);
            Assert.Empty(_logger.Entries);
        }

        [Fact]
        public async Task StartAsync_JobStorageFactoryThrows_CompletesAndLogsDebug()
        {
            // Arrange — e.g. the AddHangfire callback cannot reach the database.
            var services = new ServiceCollection();
            services.AddSingleton<JobStorage>(_ => throw new InvalidOperationException("storage unavailable"));
            var check = new HangfirePostgreSqlLockTimeoutCheck(services.BuildServiceProvider(), _logger);

            // Act
            await check.StartAsync(CancellationToken.None);

            // Assert
            var entry = Assert.Single(_logger.Entries);
            Assert.Equal(LogLevel.Debug, entry.Level);
            Assert.Equal(3161, entry.EventId.Id);
            Assert.IsType<InvalidOperationException>(entry.Exception);
        }

        [Fact]
        public void UseHangfire_CalledTwice_RegistersTheCheckOnce()
        {
            // Arrange
            var services = new ServiceCollection();
            var builder = new FlowOrchestratorBuilder(services);

            // Act
            builder.UseHangfire();
            builder.UseHangfire();

            // Assert
            Assert.Single(services, d => d.ServiceType == typeof(IHostedService)
                                      && d.ImplementationType == typeof(HangfirePostgreSqlLockTimeoutCheck));
        }

        private HangfirePostgreSqlLockTimeoutCheck CreateCheck(JobStorage storage)
        {
            var services = new ServiceCollection();
            services.AddSingleton(storage);
            return new HangfirePostgreSqlLockTimeoutCheck(services.BuildServiceProvider(), _logger);
        }

        private sealed class DerivedPostgreSqlStorage(TimeSpan invisibility, TimeSpan lockTimeout)
            : global::Hangfire.PostgreSql.PostgreSqlStorage(invisibility, lockTimeout);

        private sealed class OtherStorage : JobStorage
        {
            public TimeSpan InvisibilityTimeout => TimeSpan.FromSeconds(1);

            public override IMonitoringApi GetMonitoringApi() => throw new NotSupportedException();

            public override IStorageConnection GetConnection() => throw new NotSupportedException();
        }

        private sealed record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

        private sealed class CapturingLogger : ILogger<HangfirePostgreSqlLockTimeoutCheck>
        {
            public List<LogEntry> Entries { get; } = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
        }
    }
}

namespace Hangfire.PostgreSql
{
    using Hangfire.Storage;

    /// <summary>
    /// Stand-in with the shape the check reads from Hangfire.PostgreSql 1.21: same full type name, an
    /// internal <c>Options</c> property, and public timeout properties on the options object. The real
    /// package is pinned by the Hangfire integration tests.
    /// </summary>
    public class PostgreSqlStorage : JobStorage
    {
        private readonly PostgreSqlStorageOptions _options;

        public PostgreSqlStorage(TimeSpan invisibilityTimeout, TimeSpan distributedLockTimeout)
            => _options = new PostgreSqlStorageOptions
            {
                InvisibilityTimeout = invisibilityTimeout,
                DistributedLockTimeout = distributedLockTimeout,
            };

        public bool NullOptions { get; set; }

        public bool ThrowFromOptions { get; set; }

        internal PostgreSqlStorageOptions? Options
            => ThrowFromOptions ? throw new InvalidOperationException("options unavailable") : NullOptions ? null : _options;

        public override IMonitoringApi GetMonitoringApi() => throw new NotSupportedException();

        public override IStorageConnection GetConnection() => throw new NotSupportedException();
    }

    /// <summary>Stand-in for Hangfire.PostgreSql's options type.</summary>
    public sealed class PostgreSqlStorageOptions
    {
        public TimeSpan InvisibilityTimeout { get; set; }

        public TimeSpan DistributedLockTimeout { get; set; }
    }
}
