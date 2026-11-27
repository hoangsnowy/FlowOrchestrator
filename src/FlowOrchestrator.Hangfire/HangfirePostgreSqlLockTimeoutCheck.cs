using System.Reflection;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Warns once at startup when Hangfire's job storage is Hangfire.PostgreSql with a
/// <c>DistributedLockTimeout</c> longer than its <c>InvisibilityTimeout</c>.
/// </summary>
/// <remarks>
/// <para>
/// Hangfire.PostgreSql implements distributed locks as rows in its <c>lock</c> table. Nothing ties a
/// row to the session that wrote it, so a process killed while holding one leaves the row behind; the
/// row is ignored only once it is older than <c>DistributedLockTimeout</c> (10 min by default).
/// Hangfire's worker takes the job's <c>job:{id}:state-lock</c> for every state change, so a step job
/// whose worker is killed inside one is re-fetched after <c>InvisibilityTimeout</c> but cannot run
/// until the orphaned row expires. Each re-fetch in between parks one more worker on the lock. The
/// step is delayed, not lost: its dispatch-ledger row exists and it has no claim, which looks exactly
/// like a lost job until the lock timeout passes. Hangfire.SqlServer takes session-scoped
/// <c>sp_getapplock</c> locks instead, which the server releases when the killed connection drops.
/// </para>
/// <para>
/// The options live on an internal property of <c>PostgreSqlStorage</c>, and FlowOrchestrator does
/// not reference the community package, so they are read by name. If a future Hangfire.PostgreSql
/// moves them, the check stays silent rather than failing the host. The Hangfire integration tests
/// pin it against the version in <c>Directory.Packages.props</c>.
/// </para>
/// </remarks>
internal sealed partial class HangfirePostgreSqlLockTimeoutCheck : IHostedService
{
    /// <summary>Full name of Hangfire.PostgreSql's storage type.</summary>
    internal const string PostgreSqlStorageTypeName = "Hangfire.PostgreSql.PostgreSqlStorage";

    private readonly IServiceProvider _services;
    private readonly ILogger<HangfirePostgreSqlLockTimeoutCheck> _logger;

    /// <summary>Creates the check.</summary>
    /// <param name="services">Root provider; <see cref="JobStorage"/> is optional in it.</param>
    /// <param name="logger">Receives the warning.</param>
    public HangfirePostgreSqlLockTimeoutCheck(IServiceProvider services, ILogger<HangfirePostgreSqlLockTimeoutCheck> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>Inspects the job storage and logs the warning when the lock timeout is the longer one.</summary>
    /// <param name="cancellationToken">Unused; the check does no I/O of its own.</param>
    /// <returns>A completed task. The check never fails the host.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Check();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Runs the check once.</summary>
    /// <returns><see langword="true"/> when the warning was logged.</returns>
    internal bool Check()
    {
        JobStorage? storage;
        try
        {
            // AddHangfire registers JobStorage; resolving it runs the user's configuration callback,
            // which the Hangfire server would run moments later anyway. A failure there is Hangfire's
            // to report, so it is not rethrown from a diagnostic.
            storage = _services.GetService<JobStorage>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStorageNotInspected(ex);
            return false;
        }

        if (storage is null
            || !TryReadTimeouts(storage, out var invisibilityTimeout, out var lockTimeout)
            || lockTimeout <= invisibilityTimeout)
        {
            return false;
        }

        LogLockTimeoutExceedsInvisibility(lockTimeout, invisibilityTimeout);
        return true;
    }

    /// <summary>
    /// Reads <c>InvisibilityTimeout</c> and <c>DistributedLockTimeout</c> from a Hangfire.PostgreSql
    /// storage (or a subclass of it).
    /// </summary>
    /// <param name="storage">Any Hangfire job storage.</param>
    /// <param name="invisibilityTimeout">How long a fetched job stays invisible to other workers.</param>
    /// <param name="lockTimeout">Age after which an orphaned lock row is ignored.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="storage"/> is Hangfire.PostgreSql and both values
    /// were read; <see langword="false"/> for any other storage, or when the members are not found.
    /// </returns>
    internal static bool TryReadTimeouts(JobStorage storage, out TimeSpan invisibilityTimeout, out TimeSpan lockTimeout)
    {
        invisibilityTimeout = default;
        lockTimeout = default;

        var type = storage.GetType();
        while (type is not null && !string.Equals(type.FullName, PostgreSqlStorageTypeName, StringComparison.Ordinal))
        {
            type = type.BaseType;
        }
        if (type is null)
        {
            return false;
        }

        try
        {
            var options = type
                .GetProperty("Options", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(storage);
            if (options is null)
            {
                return false;
            }

            var optionsType = options.GetType();
            if (optionsType.GetProperty("InvisibilityTimeout")?.GetValue(options) is not TimeSpan invisibility
                || optionsType.GetProperty("DistributedLockTimeout")?.GetValue(options) is not TimeSpan locks)
            {
                return false;
            }

            invisibilityTimeout = invisibility;
            lockTimeout = locks;
            return true;
        }
        catch (Exception ex) when (ex is TargetInvocationException or MemberAccessException or AmbiguousMatchException)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 3160, Level = LogLevel.Warning,
        Message = "Hangfire.PostgreSql DistributedLockTimeout ({LockTimeout}) is longer than its InvisibilityTimeout ({InvisibilityTimeout}). " +
                  "Its locks are table rows that a killed process leaves behind, so a step job whose worker dies during a Hangfire state change " +
                  "runs again only after the lock timeout, not the invisibility timeout, and every re-fetch until then parks another worker. " +
                  "Set PostgreSqlStorageOptions.DistributedLockTimeout to at most InvisibilityTimeout.")]
    private partial void LogLockTimeoutExceedsInvisibility(TimeSpan lockTimeout, TimeSpan invisibilityTimeout);

    [LoggerMessage(EventId = 3161, Level = LogLevel.Debug,
        Message = "Hangfire job storage could not be resolved, so its lock and invisibility timeouts were not checked.")]
    private partial void LogStorageNotInspected(Exception exception);
}
