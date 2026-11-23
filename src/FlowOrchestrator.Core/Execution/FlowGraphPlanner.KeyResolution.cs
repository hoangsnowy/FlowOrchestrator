using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Step-key and scope-prefix resolution helpers for <see cref="FlowGraphPlanner"/>.
/// Bridges between the manifest's template keys and the runtime keys produced by
/// loop / foreach iteration (e.g. <c>"loop.0.child"</c> vs <c>"loop.child"</c>).
/// </summary>
public sealed partial class FlowGraphPlanner
{
    /// <summary>Longest composed <c>"{scope}.{child}"</c> key built on the stack rather than the heap.</summary>
    private const int StackKeyBufferLength = 256;

    /// <summary>
    /// Returns the step keys <see cref="Evaluate"/> must classify — every known key that has no status
    /// row yet — in ordinal order, together with the full known-key list for
    /// <see cref="FlowGraphEvaluation.AllKnownStepKeys"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The known-key space is the manifest's top-level keys, every runtime key, and every child of
    /// every runtime scope instance (<c>"loop.5"</c> expands to <c>"loop.5.child_0"</c>, …). The
    /// previous form materialised that whole space into a <see cref="SortedSet{T}"/> on every call —
    /// re-splitting every runtime key, re-building every expanded child string, and sorting them all —
    /// only for <see cref="Evaluate"/> to skip every key that already had a status (#189: 275 µs /
    /// 417 KB per call at a 500-iteration loop). Runtime keys are status keys by definition, so the
    /// keys <see cref="Evaluate"/> actually classifies are only (manifest keys ∪ expanded children)
    /// minus the status map — a set that stays small however long the run is.
    /// </para>
    /// <para>
    /// <b>Complexity per step completion: O(n)</b> in the status-map size n, with no sort over n and
    /// no per-key allocation — the same order as reading the status map itself, which the caller has
    /// already paid. Each runtime key is walked once as spans; each distinct scope instance is
    /// expanded once (deduplicated through <see cref="StepKeySlice"/> views, so the prefix string is
    /// never built); an expanded child is tested against the status map through the
    /// alternate lookup, so its string exists only when it is a candidate. Only the candidates are
    /// sorted. Sublinear needs a per-run incremental status cache — P0-1 of #189, out of scope here.
    /// </para>
    /// <para>
    /// <see cref="FlowGraphEvaluation.AllKnownStepKeys"/> is a public contract no engine path reads,
    /// so it is materialised lazily from a snapshot of the status keys; the result is identical to
    /// the eager form.
    /// </para>
    /// </remarks>
    private static (IReadOnlyList<string> Candidates, IReadOnlyList<string> AllKnown) PlanCandidateStepKeys(
        IFlowDefinition flow,
        IReadOnlyDictionary<string, StepStatus> statuses)
    {
        var cache = GetManifestCache(flow);

        // Fast path: a flow without loops/foreach has runtime keys that are all already in the
        // manifest. The known-key set is then exactly the manifest's sorted keys — return the
        // cached array directly, as both the candidate list and the known list.
        if (!AnyRuntimeKeyOutsideManifest(cache, statuses))
        {
            return (cache.SortedKeys, cache.SortedKeys);
        }

        // A dotted or empty top-level manifest key makes FindStep's exact-match shortcut reachable
        // from a multi-segment template path, which the span walk below does not model. No real
        // manifest has one; keep the original algorithm for it rather than approximate.
        if (cache.HasIrregularTopLevelKey)
        {
            var known = BuildKnownStepKeysLegacy(flow, statuses.Keys);
            return (known, known);
        }

        var candidates = new List<string>();
        // foreach rather than SortedKeys.Where(k => !statuses.ContainsKey(k)), despite CodeQL's
        // cs/linq/missed-where: the lambda captures `statuses` and would allocate a closure plus an
        // iterator on every Evaluate call, i.e. on every step completion (CLAUDE.md hot-path rule).
        foreach (var manifestKey in cache.SortedKeys)
        {
            if (!statuses.ContainsKey(manifestKey))
            {
                candidates.Add(manifestKey);
            }
        }

        Dictionary<string, StepStatus>.AlternateLookup<ReadOnlySpan<char>> statusLookup = default;
        var hasStatusLookup = statuses is Dictionary<string, StepStatus> statusDictionary
            && statusDictionary.TryGetAlternateLookup(out statusLookup);

        HashSet<StepKeySlice>? expandedScopes = null;
        foreach (var runtimeKey in statuses.Keys)
        {
            // A key without a dot has fewer than two segments and so no scope prefix.
            if (!runtimeKey.Contains('.', StringComparison.Ordinal))
            {
                continue;
            }

            if (!IsRegularKey(runtimeKey))
            {
                // Empty or whitespace-padded segments: the original Split(RemoveEmptyEntries |
                // TrimEntries) normalises these, so reuse it verbatim to keep the expanded keys
                // byte-identical. Never produced by the engine itself.
                foreach (var (scopePrefix, scopedMetadata) in ExtractRuntimeScopePrefixes(flow, runtimeKey))
                {
                    var prefixSlice = StepKeySlice.Of(scopePrefix);
                    expandedScopes ??= new HashSet<StepKeySlice>(StepKeySliceComparer.Instance);
                    if (expandedScopes.Add(prefixSlice))
                    {
                        AddMissingChildren(prefixSlice, scopedMetadata, statuses, candidates, hasStatusLookup, statusLookup);
                    }
                }

                continue;
            }

            // Walk the segments once, tracking FindStep's result for the template path of the
            // segments seen so far (numeric segments dropped). A numeric segment that follows a
            // resolved scoped step closes a scope prefix — exactly the pairs ExtractRuntimeScopePrefixes
            // yields, without the Split array, the Join strings, or a FindStep per prefix.
            StepMetadata? resolved = null;
            var anyTemplateSegment = false;
            var templateAlive = true;
            var position = 0;
            while (position <= runtimeKey.Length)
            {
                var dot = runtimeKey.IndexOf('.', position);
                var end = dot < 0 ? runtimeKey.Length : dot;
                var segment = runtimeKey.AsSpan(position, end - position);

                if (int.TryParse(segment, out _))
                {
                    if (position > 0 && templateAlive && anyTemplateSegment && resolved is IScopedStep scoped)
                    {
                        var prefixSlice = new StepKeySlice(runtimeKey, end);
                        expandedScopes ??= new HashSet<StepKeySlice>(StepKeySliceComparer.Instance);
                        if (expandedScopes.Add(prefixSlice))
                        {
                            AddMissingChildren(prefixSlice, scoped, statuses, candidates, hasStatusLookup, statusLookup);
                        }
                    }

                    // Numeric segments are removed from the template path: state is unchanged.
                }
                else if (templateAlive && dot >= 0)
                {
                    // The last segment is never resolved: only a later numeric segment reads the
                    // result, and there is none.
                    resolved = ResolveNextTemplateSegment(flow.Manifest.Steps, resolved, anyTemplateSegment, segment);
                    anyTemplateSegment = true;
                    templateAlive = resolved is not null;
                }

                if (dot < 0)
                {
                    break;
                }

                position = dot + 1;
            }
        }

        candidates.Sort(StringComparer.Ordinal);
        RemoveAdjacentDuplicates(candidates);

        return (candidates, new LazyKnownStepKeys(cache.SortedKeys, SnapshotKeys(statuses), candidates));
    }

    /// <summary>
    /// Returns <see langword="true"/> when some status key is not a top-level manifest key, i.e. the
    /// run carries runtime scope keys. A <see langword="foreach"/> rather than <c>Any(lambda)</c>:
    /// this runs on every <see cref="Evaluate"/> call and the closure would allocate each time.
    /// </summary>
    private static bool AnyRuntimeKeyOutsideManifest(ManifestKeyCache cache, IReadOnlyDictionary<string, StepStatus> statuses)
    {
        foreach (var runtimeKey in statuses.Keys)
        {
            if (!cache.KeySet.Contains(runtimeKey))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Advances FindStep's walk by one non-numeric template segment: the first segment is looked up
    /// in the manifest's top level, every later one in the steps of the scope resolved so far.
    /// </summary>
    /// <returns>The metadata for the extended template path, or <see langword="null"/> when it does not resolve.</returns>
    private static StepMetadata? ResolveNextTemplateSegment(
        StepCollection topLevel,
        StepMetadata? resolvedSoFar,
        bool anyTemplateSegment,
        ReadOnlySpan<char> segment)
    {
        if (!anyTemplateSegment)
        {
            return TryGetStep(topLevel, segment, out var top) ? top : null;
        }

        // FindStep rejects an intermediate segment that is not a scope with children.
        if (resolvedSoFar is not IScopedStep { Steps: { Count: > 0 } children })
        {
            return null;
        }

        return TryGetStep(children, segment, out var child) ? child : null;
    }

    private static bool TryGetStep(StepCollection steps, ReadOnlySpan<char> segment, out StepMetadata? step)
    {
        if (steps.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup))
        {
            return lookup.TryGetValue(segment, out step);
        }
        return steps.TryGetValue(segment.ToString(), out step);
    }

    /// <summary>
    /// Adds every child of the scope instance <paramref name="scopePrefix"/> that has no status row
    /// to <paramref name="candidates"/>. The composed <c>"{scope}.{child}"</c> string is built only
    /// for a child that is actually missing; present children are tested as spans.
    /// </summary>
    private static void AddMissingChildren(
        StepKeySlice scopePrefix,
        IScopedStep scope,
        IReadOnlyDictionary<string, StepStatus> statuses,
        List<string> candidates,
        bool hasStatusLookup,
        Dictionary<string, StepStatus>.AlternateLookup<ReadOnlySpan<char>> statusLookup)
    {
        var prefix = scopePrefix.Head.AsSpan(0, scopePrefix.HeadLength);
        // "{prefix}." is written once; each child overwrites only the tail of the buffer.
        Span<char> buffer = stackalloc char[StackKeyBufferLength];
        var useBuffer = hasStatusLookup && prefix.Length + 1 < StackKeyBufferLength;
        if (useBuffer)
        {
            prefix.CopyTo(buffer);
            buffer[prefix.Length] = '.';
        }
        foreach (var child in scope.Steps.Keys)
        {
            var length = prefix.Length + 1 + child.Length;
            if (useBuffer && length <= StackKeyBufferLength)
            {
                child.AsSpan().CopyTo(buffer[(prefix.Length + 1)..]);
                var composed = buffer[..length];
                if (!statusLookup.ContainsKey(composed))
                {
                    candidates.Add(composed.ToString());
                }

                continue;
            }
            var key = string.Concat(prefix, ".", child);
            if (!statuses.ContainsKey(key))
            {
                candidates.Add(key);
            }
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when every dot-separated segment of <paramref name="key"/> is
    /// non-empty and carries no leading or trailing whitespace — the keys for which
    /// <c>Split(RemoveEmptyEntries | TrimEntries)</c> + <c>Join</c> reproduces substrings of the input.
    /// </summary>
    private static bool IsRegularKey(string key)
    {
        var span = key.AsSpan();
        while (true)
        {
            var dot = span.IndexOf('.');
            var segment = dot < 0 ? span : span[..dot];
            if (segment.IsEmpty || char.IsWhiteSpace(segment[0]) || char.IsWhiteSpace(segment[^1]))
            {
                return false;
            }

            if (dot < 0)
            {
                return true;
            }

            span = span[(dot + 1)..];
        }
    }

    /// <summary>Removes adjacent duplicates from an ordinally sorted list in place.</summary>
    private static void RemoveAdjacentDuplicates(List<string> sorted)
    {
        if (sorted.Count < 2)
        {
            return;
        }

        var write = 1;
        for (var read = 1; read < sorted.Count; read++)
        {
            if (!string.Equals(sorted[read], sorted[write - 1], StringComparison.Ordinal))
            {
                sorted[write++] = sorted[read];
            }
        }

        sorted.RemoveRange(write, sorted.Count - write);
    }

    private static string[] SnapshotKeys(IReadOnlyDictionary<string, StepStatus> statuses)
    {
        var keys = new string[statuses.Count];
        if (statuses.Keys is ICollection<string> collection)
        {
            collection.CopyTo(keys, 0);
            return keys;
        }

        var i = 0;
        foreach (var key in statuses.Keys)
        {
            keys[i++] = key;
        }

        return keys;
    }

    /// <summary>
    /// The full known-key list (manifest keys ∪ status keys ∪ missing expanded children) in ordinal
    /// order, materialised on first access — no engine path reads it, and building it eagerly was the
    /// sort and copy that dominated <see cref="Evaluate"/>.
    /// </summary>
    /// <remarks>
    /// Every expanded child either has a status row (and is in the status snapshot) or is one of the
    /// candidates, so the union below is exactly the eager known-key set.
    /// </remarks>
    private sealed class LazyKnownStepKeys(string[] manifestKeys, string[] statusKeys, List<string> candidates)
        : IReadOnlyList<string>
    {
        private string[]? _items;

        private string[] Items
        {
            get
            {
                var items = Volatile.Read(ref _items);
                if (items is not null)
                {
                    return items;
                }

                var known = new SortedSet<string>(manifestKeys, StringComparer.Ordinal);
                known.UnionWith(statusKeys);
                known.UnionWith(candidates);
                items = [.. known];
                return Interlocked.CompareExchange(ref _items, items, null) ?? items;
            }
        }

        public string this[int index] => Items[index];

        public int Count => Items.Length;

        public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)Items).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// The original eager known-key construction, retained only for manifests with a dotted or empty
    /// top-level key (see <see cref="PlanCandidateStepKeys"/>).
    /// </summary>
    private static IReadOnlyList<string> BuildKnownStepKeysLegacy(IFlowDefinition flow, IEnumerable<string> runtimeStepKeys)
    {
        var cache = GetManifestCache(flow);
        var known = new SortedSet<string>(cache.KeySet, StringComparer.Ordinal);
        HashSet<string>? expandedScopes = null;
        foreach (var runtimeKey in runtimeStepKeys)
        {
            known.Add(runtimeKey);

            foreach (var (scopePrefix, scopedMetadata) in ExtractRuntimeScopePrefixes(flow, runtimeKey))
            {
                expandedScopes ??= new HashSet<string>(StringComparer.Ordinal);
                if (!expandedScopes.Add(scopePrefix))
                {
                    continue;
                }

                foreach (var child in scopedMetadata.Steps.Keys)
                {
                    known.Add($"{scopePrefix}.{child}");
                }
            }
        }

        return known.ToArray();
    }

    private static IEnumerable<(string RuntimeScopePrefix, IScopedStep ScopedMetadata)> ExtractRuntimeScopePrefixes(
        IFlowDefinition flow,
        string runtimeStepKey)
    {
        var segments = runtimeStepKey
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length < 2)
        {
            yield break;
        }

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!int.TryParse(segments[i + 1], out _))
            {
                continue;
            }

            // string.Join's array-range overload instead of Take(): same output, no LINQ iterator,
            // and it returns the existing string instance when the range is a single segment.
            var runtimeTemplatePath = string.Join('.', segments, 0, i + 1);
            var templatePath = RemoveNumericSegments(runtimeTemplatePath);
            var metadata = flow.Manifest.Steps.FindStep(templatePath);
            if (metadata is not IScopedStep scoped)
            {
                continue;
            }

            var runtimeScopePrefix = string.Join('.', segments, 0, i + 2);
            yield return (runtimeScopePrefix, scoped);
        }
    }

    private static StepMetadata? ResolveMetadata(IFlowDefinition flow, string runtimeStepKey)
    {
        var metadata = flow.Manifest.Steps.FindStep(runtimeStepKey);
        if (metadata is not null)
        {
            return metadata;
        }

        var templateKey = RemoveNumericSegments(runtimeStepKey);
        return flow.Manifest.Steps.FindStep(templateKey);
    }

    /// <summary>
    /// Resolves a dependency key to its runtime equivalent by prepending the current step's
    /// loop-scope prefix when the dependency is a sibling step (no dot, same scope level).
    /// </summary>
    private static string ResolveRuntimeDependencyKey(string runtimeStepKey, string dependencyKey)
    {
        if (string.IsNullOrWhiteSpace(dependencyKey))
        {
            return dependencyKey;
        }

        if (dependencyKey.Contains('.', StringComparison.Ordinal))
        {
            return dependencyKey;
        }

        var lastDot = runtimeStepKey.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return dependencyKey;
        }

        var runtimeParentPath = runtimeStepKey[..lastDot];
        return $"{runtimeParentPath}.{dependencyKey}";
    }

    /// <summary>
    /// Resolves a dependency key to its template (non-runtime) equivalent, removing
    /// numeric iteration segments so it can be matched against the manifest definition.
    /// </summary>
    private static string ResolveTemplateDependencyKey(string stepKey, string dependencyKey)
    {
        if (string.IsNullOrWhiteSpace(dependencyKey))
        {
            return dependencyKey;
        }

        if (dependencyKey.Contains('.', StringComparison.Ordinal))
        {
            return RemoveNumericSegments(dependencyKey);
        }

        var lastDot = stepKey.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return dependencyKey;
        }

        var parentPath = stepKey[..lastDot];
        return $"{parentPath}.{dependencyKey}";
    }

    /// <summary>Removes numeric loop-iteration segments from a dot-separated step key.</summary>
    private static string RemoveNumericSegments(string key)
    {
        // Fast path: the split/rejoin would reproduce the input verbatim, so return the existing
        // instance. Template keys — the overwhelming majority of calls — carry no numeric segment.
        if (!NeedsSegmentRewrite(key))
        {
            return key;
        }

        var segments = key
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => !int.TryParse(segment, out _));

        return string.Join('.', segments);
    }

    /// <summary>
    /// Returns <see langword="true"/> when splitting <paramref name="key"/> on <c>'.'</c> with
    /// <see cref="StringSplitOptions.RemoveEmptyEntries"/> + <see cref="StringSplitOptions.TrimEntries"/>,
    /// dropping numeric segments and rejoining would produce a string different from the input —
    /// i.e. some segment is a loop index, empty, or padded with whitespace.
    /// </summary>
    private static bool NeedsSegmentRewrite(string key)
    {
        var span = key.AsSpan();
        var start = 0;

        while (true)
        {
            var dot = span[start..].IndexOf('.');
            var end = dot < 0 ? span.Length : start + dot;
            var segment = span[start..end];

            if (segment.Length == 0
                || char.IsWhiteSpace(segment[0])
                || char.IsWhiteSpace(segment[^1])
                || int.TryParse(segment, out _))
            {
                return true;
            }

            if (dot < 0)
            {
                return false;
            }

            start = end + 1;
        }
    }

    private static bool IsFinal(StepStatus status) =>
        status is StepStatus.Succeeded or StepStatus.Failed or StepStatus.Skipped;

    private static StepInstance CreateStepInstance(
        IExecutionContext context,
        string key,
        string type,
        IDictionary<string, object?> inputs)
    {
        return new StepInstance(key, type)
        {
            RunId = context.RunId,
            PrincipalId = context.PrincipalId,
            TriggerData = context.TriggerData,
            TriggerHeaders = context.TriggerHeaders,
            ScheduledTime = DateTimeOffset.UtcNow,
            Inputs = new Dictionary<string, object?>(inputs)
        };
    }
}
