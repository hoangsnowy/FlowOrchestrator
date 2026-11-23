using System.Text.Json;
using FlowOrchestrator.Core.Expressions;

namespace FlowOrchestrator.Core.Execution.Internal;

/// <summary>
/// Resolves <c>@triggerBody()</c>, <c>@triggerHeaders()</c>, and <c>@steps('key').output|status|error</c>
/// expressions in a step's input dictionary in a <b>single walk</b>, used by
/// <see cref="DefaultStepExecutor"/> once per step execution.
/// </summary>
/// <remarks>
/// <para>
/// <b>Classify once, never re-scan.</b> Each input leaf is classified exactly once, from its own
/// text, as a trigger expression, a step expression, or a literal. Whatever an expression
/// resolves to is <i>data</i>: it is placed in the result as-is and never walked again. Resolution
/// used to run as two independent passes — triggers first, then <c>@steps()</c> over the output of
/// the first pass — so a trigger payload value that happened to read like an <c>@steps()</c>
/// expression was evaluated by the second pass.
/// </para>
/// <para>
/// <b>Only manifest text is an expression.</b> When the step's manifest inputs are supplied, a
/// leaf is evaluated only while it is still the manifest's own text at that position. A step
/// re-dispatched with its already-resolved inputs (the <see cref="Abstractions.StepStatus.Pending"/> poll path
/// hands the runtime the same instance, so poll state survives) therefore sees its substituted
/// values as data on every later attempt, instead of evaluating a substituted string the second
/// time round. Top-level keys the manifest does not declare (inputs a test or a custom fan-out
/// handler built by hand) keep the historical behaviour and are resolved; keys the manifest
/// declares are matched ordinally first, then case-insensitively, because a typed handler writes
/// its inputs back under its serializer's property names.
/// </para>
/// <para>
/// Complexity per step execution: O(L) over the input leaves L, plus one manifest lookup per
/// visited node — O(1) against a dictionary manifest, O(p) for a <see cref="JsonElement"/>
/// object with p properties. The case-insensitive fallback is O(k) over the k top-level manifest
/// keys and runs only on an ordinal miss. Nothing is allocated beyond the result dictionaries the
/// resolution itself needs, and inputs with nothing to resolve are returned unchanged. This
/// replaces two walks (the second of which always copied the dictionary) with one.
/// </para>
/// </remarks>
internal static class InputResolutionPipeline
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Returns a dictionary with all <c>@triggerBody()</c> / <c>@triggerHeaders()</c> expressions
    /// resolved against the supplied trigger data; <c>@steps()</c> expressions are left verbatim.
    /// Returns the original dictionary unchanged when no value could possibly need resolution.
    /// </summary>
    /// <param name="inputs">The raw input dictionary from the step instance.</param>
    /// <param name="triggerData">The trigger payload, if any.</param>
    /// <param name="triggerHeaders">The trigger header map, if any.</param>
    /// <remarks>
    /// Trigger-only entry point for callers with no run context to read step outputs from.
    /// Without a step resolver the walk never awaits, so it always completes synchronously.
    /// </remarks>
    public static IDictionary<string, object?> Resolve(
        IDictionary<string, object?> inputs,
        object? triggerData,
        IReadOnlyDictionary<string, string>? triggerHeaders)
    {
        var pending = ResolveAsync(inputs, manifestInputs: null, triggerData, triggerHeaders, stepResolver: null);
        return pending.IsCompletedSuccessfully
            ? pending.Result
            : pending.AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Returns a dictionary with every trigger and step expression in <paramref name="inputs"/>
    /// resolved, classifying each leaf once and never re-scanning a substituted value.
    /// </summary>
    /// <param name="inputs">The step instance's input dictionary.</param>
    /// <param name="manifestInputs">
    /// The step's manifest inputs. A leaf is only evaluated as an expression while it still equals
    /// the manifest's text at the same position; see the type remarks. Pass <see langword="null"/>
    /// to treat every leaf as manifest text.
    /// </param>
    /// <param name="triggerData">The trigger payload, if any.</param>
    /// <param name="triggerHeaders">The trigger header map, if any.</param>
    /// <param name="stepResolver">
    /// Resolves <c>@steps()</c> expressions; <see langword="null"/> leaves them verbatim.
    /// </param>
    /// <returns>The resolved inputs, or <paramref name="inputs"/> itself when nothing needed resolving.</returns>
    public static ValueTask<IDictionary<string, object?>> ResolveAsync(
        IDictionary<string, object?> inputs,
        IDictionary<string, object?>? manifestInputs,
        object? triggerData,
        IReadOnlyDictionary<string, string>? triggerHeaders,
        StepOutputResolver? stepResolver)
    {
        var scope = new ResolutionScope(triggerData, triggerHeaders, stepResolver);
        return ResolveDictionaryAsync(inputs, ManifestCursor.ForRoot(manifestInputs), scope, isRoot: true);
    }

    private static async ValueTask<IDictionary<string, object?>> ResolveDictionaryAsync(
        IDictionary<string, object?> inputs,
        ManifestCursor manifest,
        ResolutionScope scope,
        bool isRoot = false)
    {
        if (inputs.Count == 0)
            return inputs;

        // Most steps have purely literal inputs (numbers, plain strings, bools). Pre-scan the
        // values: if none can possibly be — or contain — an expression, return the original
        // dictionary unchanged. This keeps the common case zero-allocation.
        // Only at the root: the top-level dictionary is the step instance's own copy, but a nested
        // dictionary is still the manifest's object, shared by every run of the flow. Copying it
        // keeps a handler that mutates its nested inputs from rewriting the flow definition (the
        // second resolution pass used to guarantee that copy).
        if (isRoot && !ContainsResolvableValue(inputs))
        {
            return inputs;
        }

        var resolved = new Dictionary<string, object?>(inputs.Count);
        foreach (var (key, value) in inputs)
        {
            // __loopItem / __loopIndex carry iteration DATA the engine injected, not manifest
            // expressions. Resolving them would rewrite an item that merely happens to be a string
            // beginning with '@', and would reshape a list item into object[] — and only for the
            // children ForEachStepHandler fans out, since the children rebuilt from the loop's
            // template get their context injected after this pass. Two children of the same
            // iteration would then see different values. Pass them through untouched.
            resolved[key] = LoopScopeInputs.IsReservedKey(key)
                ? value
                : await ResolveValueAsync(value, manifest.Child(key), scope).ConfigureAwait(false);
        }

        return resolved;
    }

    /// <summary>
    /// Returns <see langword="true"/> when at least one value in <paramref name="inputs"/> could
    /// plausibly need expression resolution — a string starting with <c>@</c>, a
    /// <see cref="JsonElement"/>, or a nested collection.
    /// </summary>
    private static bool ContainsResolvableValue(IDictionary<string, object?> inputs)
    {
        foreach (var (key, value) in inputs)
        {
            // Reserved loop-context keys are never resolved, so they must not drag the whole
            // dictionary off the zero-allocation fast path either.
            if (LoopScopeInputs.IsReservedKey(key))
            {
                continue;
            }

            switch (value)
            {
                case string s when StartsWithAt(s):
                    return true;
                case JsonElement:
                case IDictionary<string, object?>:
                case IEnumerable<object?>:
                    // Nested structures may contain expressions — fall through
                    // to the full resolution path.
                    return true;
                default:
                    continue;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the first non-whitespace character of
    /// <paramref name="value"/> is <c>@</c>. Every expression form starts that way, so this
    /// rejects literal strings without a <c>Trim</c> allocation.
    /// </summary>
    private static bool StartsWithAt(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsWhiteSpace(c))
            {
                continue;
            }
            return c == '@';
        }
        return false;
    }

    private static async ValueTask<object?> ResolveValueAsync(object? value, ManifestCursor manifest, ResolutionScope scope)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                if (StartsWithAt(s) && manifest.IsManifestText(s))
                {
                    var (matched, resolved) = await TryResolveExpressionAsync(s, scope).ConfigureAwait(false);
                    if (matched)
                        return resolved;
                }
                return s;
            case JsonElement element:
                return await ResolveJsonElementAsync(element, manifest, scope).ConfigureAwait(false);
            case IDictionary<string, object?> dict:
                return await ResolveDictionaryAsync(dict, manifest, scope).ConfigureAwait(false);
            case IEnumerable<object?> sequence:
                return await ResolveSequenceAsync(sequence, manifest, scope).ConfigureAwait(false);
            default:
                return value;
        }
    }

    private static async ValueTask<object?> ResolveJsonElementAsync(JsonElement element, ManifestCursor manifest, ResolutionScope scope)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (element.ValueKind == JsonValueKind.String)
        {
            var stringValue = element.GetString();
            if (StartsWithAt(stringValue) && manifest.IsManifestText(stringValue!))
            {
                var (matched, resolved) = await TryResolveExpressionAsync(stringValue!, scope).ConfigureAwait(false);
                if (matched)
                    return resolved;
            }
            return CloneOrNull(element);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var objectValue = JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText(), _jsonOptions)
                              ?? new Dictionary<string, object?>();
            return await ResolveDictionaryAsync(objectValue, manifest, scope).ConfigureAwait(false);
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var arrayValue = JsonSerializer.Deserialize<List<object?>>(element.GetRawText(), _jsonOptions) ?? [];
            return await ResolveSequenceAsync(arrayValue, manifest, scope).ConfigureAwait(false);
        }

        return CloneOrNull(element);
    }

    private static async ValueTask<object?[]> ResolveSequenceAsync(IEnumerable<object?> sequence, ManifestCursor manifest, ResolutionScope scope)
    {
        // Materialise a non-indexable manifest sequence once so the per-item lookup stays O(1).
        manifest = manifest.WithIndexableSequence();

        var items = sequence is ICollection<object?> collection
            ? new List<object?>(collection.Count)
            : new List<object?>();
        var index = 0;
        foreach (var item in sequence)
        {
            items.Add(await ResolveValueAsync(item, manifest.Item(index), scope).ConfigureAwait(false));
            index++;
        }

        return items.ToArray();
    }

    /// <summary>
    /// Classifies <paramref name="expression"/> once and resolves it. The resolved value is data —
    /// the caller places it in the result without walking it again.
    /// </summary>
    private static async ValueTask<(bool Matched, object? Value)> TryResolveExpressionAsync(string expression, ResolutionScope scope)
    {
        if (TriggerExpressionResolver.TryResolveTriggerBodyExpression(expression, scope.TriggerData, out var resolvedBody))
            return (true, resolvedBody);

        if (TriggerExpressionResolver.TryResolveTriggerHeadersExpression(expression, scope.TriggerHeaders, out var resolvedHeader))
            return (true, resolvedHeader);

        if (scope.StepResolver is not null && StepOutputResolver.IsStepExpression(expression))
            return (true, await scope.StepResolver.ResolveAsync(expression).ConfigureAwait(false));

        return (false, null);
    }

    private static JsonElement? CloneOrNull(JsonElement element)
    {
        try
        {
            return element.Clone();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The run-scoped values every leaf of one resolution walk is resolved against.</summary>
    private readonly record struct ResolutionScope(
        object? TriggerData,
        IReadOnlyDictionary<string, string>? TriggerHeaders,
        StepOutputResolver? StepResolver);

    /// <summary>
    /// Tracks the manifest node that corresponds to the input node currently being walked, so a
    /// leaf can be checked against the manifest's own text at the same position.
    /// </summary>
    private readonly struct ManifestCursor
    {
        private enum CursorKind : byte
        {
            /// <summary>No manifest to compare against — every leaf counts as manifest text.</summary>
            Unconstrained = 0,

            /// <summary>The top-level manifest input dictionary.</summary>
            Root,

            /// <summary>A manifest node exists at this position.</summary>
            Present,

            /// <summary>The manifest has nothing at this position — every leaf below is data.</summary>
            Absent,
        }

        private readonly CursorKind _kind;
        private readonly object? _node;

        private ManifestCursor(CursorKind kind, object? node)
        {
            _kind = kind;
            _node = node;
        }

        private static ManifestCursor AbsentCursor => new(CursorKind.Absent, null);

        public static ManifestCursor ForRoot(IDictionary<string, object?>? manifestInputs)
            => manifestInputs is null ? default : new ManifestCursor(CursorKind.Root, manifestInputs);

        /// <summary>Returns the cursor for the child named <paramref name="key"/>.</summary>
        public ManifestCursor Child(string key)
        {
            switch (_kind)
            {
                case CursorKind.Unconstrained:
                case CursorKind.Absent:
                    return this;

                case CursorKind.Root:
                    // A top-level key the manifest does not declare was put there by hand (a test,
                    // a custom fan-out handler) — keep resolving it as before.
                    return TryGetMember((IDictionary<string, object?>)_node!, key, out var member)
                        ? new ManifestCursor(CursorKind.Present, member)
                        : default;

                default:
                    return _node switch
                    {
                        IDictionary<string, object?> dict when dict.TryGetValue(key, out var child)
                            => new ManifestCursor(CursorKind.Present, child),
                        JsonElement { ValueKind: JsonValueKind.Object } element when element.TryGetProperty(key, out var property)
                            => new ManifestCursor(CursorKind.Present, property),
                        _ => AbsentCursor,
                    };
            }
        }

        /// <summary>Returns the cursor for the sequence item at <paramref name="index"/>.</summary>
        public ManifestCursor Item(int index)
        {
            if (_kind != CursorKind.Present)
                return _kind == CursorKind.Unconstrained ? this : AbsentCursor;

            return _node switch
            {
                JsonElement { ValueKind: JsonValueKind.Array } element when index < element.GetArrayLength()
                    => new ManifestCursor(CursorKind.Present, element[index]),
                IList<object?> list when index < list.Count
                    => new ManifestCursor(CursorKind.Present, list[index]),
                IReadOnlyList<object?> list when index < list.Count
                    => new ManifestCursor(CursorKind.Present, list[index]),
                _ => AbsentCursor,
            };
        }

        /// <summary>
        /// Returns a cursor whose manifest sequence (if any) supports indexed access, materialising
        /// a lazily enumerated manifest sequence once.
        /// </summary>
        public ManifestCursor WithIndexableSequence()
            => _kind == CursorKind.Present
               && _node is IEnumerable<object?> sequence and not IList<object?> and not IReadOnlyList<object?>
                ? new ManifestCursor(CursorKind.Present, sequence.ToArray())
                : this;

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="text"/> is the manifest's own text at
        /// this position, i.e. it was authored in the flow definition rather than substituted in.
        /// </summary>
        public bool IsManifestText(string text)
        {
            switch (_kind)
            {
                case CursorKind.Unconstrained:
                    return true;
                case CursorKind.Present:
                    return _node switch
                    {
                        string s => string.Equals(s, text, StringComparison.Ordinal),
                        JsonElement { ValueKind: JsonValueKind.String } element => element.ValueEquals(text),
                        _ => false,
                    };
                default:
                    return false;
            }
        }

        /// <summary>
        /// Looks <paramref name="key"/> up ordinally, then case-insensitively. O(1) on a hit; the
        /// fallback is O(k) over the manifest keys and runs only on an ordinal miss.
        /// </summary>
        private static bool TryGetMember(IDictionary<string, object?> manifest, string key, out object? member)
        {
            if (manifest.TryGetValue(key, out member))
                return true;

            foreach (var (candidate, value) in manifest)
            {
                if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
                {
                    member = value;
                    return true;
                }
            }

            member = null;
            return false;
        }
    }
}
