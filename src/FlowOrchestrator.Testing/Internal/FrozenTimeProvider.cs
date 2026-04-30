namespace FlowOrchestrator.Testing.Internal;

/// <summary>
/// <see cref="TimeProvider"/> whose <see cref="GetUtcNow"/> returns a value the test controls
/// via <see cref="Advance"/>. Used by <see cref="FlowTestHostBuilder{TFlow}.WithSystemClock"/>
/// to freeze the clock the in-memory cron dispatcher reads from.
/// </summary>
internal sealed class FrozenTimeProvider : TimeProvider
{
    private long _ticks;

    public FrozenTimeProvider(DateTimeOffset now)
    {
        _ticks = now.UtcTicks;
    }

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow()
    {
        var ticks = Interlocked.Read(ref _ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    /// <summary>Advances the frozen clock by <paramref name="delta"/>.</summary>
    public void Advance(TimeSpan delta)
    {
        Interlocked.Add(ref _ticks, delta.Ticks);
    }
}
