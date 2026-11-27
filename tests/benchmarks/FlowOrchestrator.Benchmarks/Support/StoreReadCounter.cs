using System.Reflection;
using System.Runtime.ExceptionServices;
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Benchmarks.Support;

/// <summary>
/// Tallies what the engine reads from its stores: every call, and the rows each collection-returning
/// read hands back. Shared by the <see cref="CountingStoreProxy{T}"/> instances wrapped around one store.
/// </summary>
/// <remarks>
/// Rows are what a SQL backend would have to transfer and the engine would have to materialise, so a
/// row count that grows with the run's size per step completion is the O(n²) signal — independent of
/// machine speed, which is why the stress harness and these benchmarks report it next to wall time.
/// </remarks>
public sealed class StoreReadCounter
{
    private long _calls;
    private long _statusRows;
    private long _keySetRows;
    private long _statusReads;

    /// <summary>Store calls of every kind, reads and writes.</summary>
    public long Calls => Interlocked.Read(ref _calls);

    /// <summary>Rows returned by every <c>GetStepStatusesAsync</c> overload.</summary>
    public long StatusRows => Interlocked.Read(ref _statusRows);

    /// <summary>Number of <c>GetStepStatusesAsync</c> calls, any overload.</summary>
    public long StatusReads => Interlocked.Read(ref _statusReads);

    /// <summary>Rows returned by the full-set claim and dispatch-ledger reads.</summary>
    public long KeySetRows => Interlocked.Read(ref _keySetRows);

    /// <summary>Records one store call and, when its result is a completed collection, the rows it returned.</summary>
    /// <param name="method">The interface member that was invoked.</param>
    /// <param name="result">The value the store returned.</param>
    internal void Record(MethodInfo method, object? result)
    {
        Interlocked.Increment(ref _calls);
        switch (result)
        {
            case Task<IReadOnlyDictionary<string, StepStatus>> statuses:
                Interlocked.Increment(ref _statusReads);
                Interlocked.Add(ref _statusRows, statuses.IsCompletedSuccessfully ? statuses.Result.Count : 0);
                break;
            case Task<IReadOnlyCollection<string>> keys when method.Name.StartsWith("Get", StringComparison.Ordinal):
                Interlocked.Add(ref _keySetRows, keys.IsCompletedSuccessfully ? keys.Result.Count : 0);
                break;
            case Task<IReadOnlySet<string>> set when method.Name.StartsWith("Get", StringComparison.Ordinal):
                Interlocked.Add(ref _keySetRows, set.IsCompletedSuccessfully ? set.Result.Count : 0);
                break;
        }
    }
}

/// <summary>
/// A <see cref="DispatchProxy"/> that forwards every member of <typeparamref name="T"/> to a real store and
/// reports the call to a <see cref="StoreReadCounter"/>.
/// </summary>
/// <typeparam name="T">The store interface being counted.</typeparam>
/// <remarks>
/// A proxy rather than a hand-written decorator, so the same counting code measures the engine before and
/// after a change to the store interfaces: members added later are forwarded without touching this file.
/// Counting assumes the wrapped store returns completed tasks for its reads, which the in-memory store does.
/// </remarks>
public class CountingStoreProxy<T> : DispatchProxy
    where T : class
{
    private T _target = null!;
    private StoreReadCounter _counter = null!;

    /// <summary>Wraps <paramref name="target"/> so every call through the result is counted.</summary>
    /// <param name="target">The real store.</param>
    /// <param name="counter">Where calls and rows are tallied.</param>
    /// <returns>A <typeparamref name="T"/> that forwards to <paramref name="target"/>.</returns>
    public static T Wrap(T target, StoreReadCounter counter)
    {
        var proxy = Create<T, CountingStoreProxy<T>>();
        var self = (CountingStoreProxy<T>)(object)proxy;
        self._target = target;
        self._counter = counter;
        return proxy;
    }

    /// <inheritdoc/>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        object? result;
        try
        {
            result = targetMethod.Invoke(_target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        _counter.Record(targetMethod, result);
        return result;
    }
}
