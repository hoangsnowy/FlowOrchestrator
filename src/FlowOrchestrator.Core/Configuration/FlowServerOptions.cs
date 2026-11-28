namespace FlowOrchestrator.Core.Configuration;

/// <summary>
/// Configuration for the server registry: how often each process writes its heartbeat to
/// <see cref="Storage.IFlowServerStore"/>, when the dashboard calls a server stale, and when its row is purged.
/// Applied via <see cref="FlowOrchestratorBuilder.Servers"/>.
/// </summary>
/// <remarks>
/// A server is <em>stale</em> once its latest heartbeat is older than <see cref="StaleAfter"/>
/// (<see cref="HeartbeatInterval"/> × <see cref="MissedHeartbeatsBeforeStale"/>, 45 s by default): it crashed,
/// hangs, or cannot reach the store. A process that stops gracefully removes its own row; a stale one is removed
/// by whichever replica purges next, once it has been silent for <see cref="PurgeAfter"/>.
/// </remarks>
public sealed class FlowServerOptions
{
    /// <summary>The default for <see cref="HeartbeatInterval"/>: 15 seconds.</summary>
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>The default for <see cref="MissedHeartbeatsBeforeStale"/>: 3.</summary>
    public const int DefaultMissedHeartbeatsBeforeStale = 3;

    /// <summary>The default for <see cref="PurgeAfter"/>: 1 hour.</summary>
    public static readonly TimeSpan DefaultPurgeAfter = TimeSpan.FromHours(1);

    /// <summary>The shortest accepted <see cref="HeartbeatInterval"/>: 1 second.</summary>
    public static readonly TimeSpan MinHeartbeatInterval = TimeSpan.FromSeconds(1);

    /// <summary>The longest accepted <see cref="HeartbeatInterval"/>: 10 minutes.</summary>
    public static readonly TimeSpan MaxHeartbeatInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// When <see langword="false"/>, this process neither registers nor heartbeats, and is absent from the
    /// Servers view. Default <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the process writes its heartbeat.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when set below <see cref="MinHeartbeatInterval"/> or above <see cref="MaxHeartbeatInterval"/>.</exception>
    public TimeSpan HeartbeatInterval
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, MinHeartbeatInterval);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxHeartbeatInterval);
            field = value;
        }
    } = DefaultHeartbeatInterval;

    /// <summary>How many consecutive heartbeats a server may miss before the dashboard shows it as stale.</summary>
    /// <remarks>At least 2, so one slow store round trip never flips a live server to stale.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when set below 2 or above 100.</exception>
    public int MissedHeartbeatsBeforeStale
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 2);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
            field = value;
        }
    } = DefaultMissedHeartbeatsBeforeStale;

    /// <summary>Age of the latest heartbeat beyond which a server is shown as stale: <see cref="HeartbeatInterval"/> × <see cref="MissedHeartbeatsBeforeStale"/>.</summary>
    public TimeSpan StaleAfter => HeartbeatInterval * MissedHeartbeatsBeforeStale;

    /// <summary>
    /// Age of the latest heartbeat beyond which a server's row is deleted. Must be longer than
    /// <see cref="StaleAfter"/>, so a stale server stays visible for a while before it disappears.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when set to zero or a negative value.</exception>
    public TimeSpan PurgeAfter
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            field = value;
        }
    } = DefaultPurgeAfter;

    /// <summary>
    /// Runtime name this process reports (<c>Hangfire</c>, <c>InMemory</c>, <c>ServiceBus</c>). Set by
    /// <c>UseHangfire()</c>, <c>UseInMemoryRuntime()</c> and <c>UseAzureServiceBusRuntime()</c>; a custom runtime
    /// may set its own. When <see langword="null"/>, the name is derived from the registered
    /// <c>IStepDispatcher</c> type (its name without the <c>StepDispatcher</c> suffix).
    /// </summary>
    public string? RuntimeName { get; set; }

    /// <summary>Checks the settings that depend on each other.</summary>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="PurgeAfter"/> is not longer than <see cref="StaleAfter"/>.</exception>
    internal void Validate()
    {
        if (PurgeAfter <= StaleAfter)
        {
            throw new InvalidOperationException(
                $"FlowServerOptions.PurgeAfter ({PurgeAfter}) must be longer than StaleAfter ({StaleAfter} = HeartbeatInterval x MissedHeartbeatsBeforeStale); " +
                "otherwise a server is deleted before the dashboard can show it as stale.");
        }
    }
}
