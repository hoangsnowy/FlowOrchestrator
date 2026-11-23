namespace FlowOrchestrator.Core.Execution.Internal;

/// <summary>
/// A runtime step key expressed as a view over existing strings instead of a new string: the
/// first <see cref="HeadLength"/> characters of <see cref="Head"/>, optionally followed by
/// <c>'.'</c> and <see cref="Tail"/>.
/// </summary>
/// <remarks>
/// <para>
/// The engine's key arithmetic produces two shapes over and over: a dotted ancestor of a runtime
/// key (<c>"loop.3"</c> out of <c>"loop.3.child"</c>, a prefix of an existing string) and a sibling
/// resolved into runtime space (<c>"loop.3" + "." + "child_0"</c>, a concatenation of two existing
/// strings). Materialising either as a <see cref="string"/> just to hash it or compare it was the
/// allocation behind several of #189's superlinear terms. A slice covers both shapes without
/// allocating, and hashes and compares as the logical string it denotes, so two slices of
/// different shapes that spell the same key are equal.
/// </para>
/// <para>
/// Equality is ordinal, matching the <see cref="StringComparison.Ordinal"/> comparisons it
/// replaces. Use with <see cref="StepKeySliceComparer"/>.
/// </para>
/// </remarks>
internal readonly struct StepKeySlice
{
    /// <summary>Creates a slice over <c>head[..headLength]</c>, optionally followed by <c>"." + tail</c>.</summary>
    /// <param name="head">The string whose prefix is the first part of the key.</param>
    /// <param name="headLength">How many leading characters of <paramref name="head"/> belong to the key.</param>
    /// <param name="tail">An optional trailing segment, joined to the head with a <c>'.'</c>.</param>
    public StepKeySlice(string head, int headLength, string? tail = null)
    {
        Head = head;
        HeadLength = headLength;
        Tail = tail;
    }

    /// <summary>The string supplying the leading characters of the key.</summary>
    public string Head { get; }

    /// <summary>Number of leading characters of <see cref="Head"/> that belong to the key.</summary>
    public int HeadLength { get; }

    /// <summary>Optional trailing segment appended after a <c>'.'</c>; <see langword="null"/> when absent.</summary>
    public string? Tail { get; }

    /// <summary>Length of the logical key in characters.</summary>
    public int Length => Tail is null ? HeadLength : HeadLength + 1 + Tail.Length;

    /// <summary>Returns a slice covering the whole of <paramref name="key"/>.</summary>
    /// <param name="key">An existing step key.</param>
    public static StepKeySlice Of(string key) => new(key, key.Length);

    /// <summary>Returns the character at logical position <paramref name="index"/>.</summary>
    /// <param name="index">A position in <c>[0, <see cref="Length"/>)</c>.</param>
    public char this[int index]
        => index < HeadLength ? Head[index]
            : index == HeadLength ? '.'
            : Tail![index - HeadLength - 1];

    /// <summary>Materialises the logical key as a string. Diagnostics and tests only.</summary>
    /// <returns>The key the slice denotes.</returns>
    public override string ToString()
        => Tail is null
            ? Head[..HeadLength]
            : string.Concat(Head.AsSpan(0, HeadLength), ".", Tail);
}

/// <summary>
/// Ordinal equality comparer for <see cref="StepKeySlice"/> that hashes and compares the logical key,
/// so a prefix slice and a concatenation slice spelling the same key collide as they should.
/// </summary>
/// <remarks>
/// The hash is FNV-1a over UTF-16 code units — computable piecewise across the head, the joining dot
/// and the tail, which <see cref="string.GetHashCode(ReadOnlySpan{char})"/> is not. Step keys are built
/// by the engine from manifest names and iteration indices, not supplied by an adversary, so the lack
/// of hash randomisation is not a flooding concern here.
/// </remarks>
internal sealed class StepKeySliceComparer : IEqualityComparer<StepKeySlice>
{
    /// <summary>The shared comparer instance; stateless.</summary>
    public static readonly StepKeySliceComparer Instance = new();

    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    private StepKeySliceComparer()
    {
    }

    /// <inheritdoc/>
    public bool Equals(StepKeySlice x, StepKeySlice y)
    {
        if (x.Length != y.Length)
        {
            return false;
        }

        // Common case first: two plain prefixes compare as spans, vectorised by the BCL.
        if (x.Tail is null && y.Tail is null)
        {
            return x.Head.AsSpan(0, x.HeadLength).SequenceEqual(y.Head.AsSpan(0, y.HeadLength));
        }

        for (var i = 0; i < x.Length; i++)
        {
            if (x[i] != y[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public int GetHashCode(StepKeySlice obj)
    {
        var hash = Accumulate(FnvOffsetBasis, obj.Head.AsSpan(0, obj.HeadLength));
        if (obj.Tail is not null)
        {
            hash = unchecked((hash ^ '.') * FnvPrime);
            hash = Accumulate(hash, obj.Tail);
        }

        return unchecked((int)hash);
    }

    private static uint Accumulate(uint hash, ReadOnlySpan<char> chars)
    {
        foreach (var c in chars)
        {
            hash = unchecked((hash ^ c) * FnvPrime);
        }

        return hash;
    }
}
