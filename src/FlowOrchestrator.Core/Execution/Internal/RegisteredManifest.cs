using System.Runtime.CompilerServices;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution.Internal;

/// <summary>
/// The persisted form of a flow's manifest — what <c>FlowSyncHostedService</c> writes to
/// <see cref="Storage.FlowDefinitionRecord.ManifestJson"/> at startup — and the comparison that tells a
/// worker whether the manifest it holds is the one most recently registered for the flow.
/// </summary>
/// <remarks>
/// <para>
/// Every host syncs its own manifest to the flow store when it starts, so during a rolling deploy the
/// stored manifest is the one of the most recently started replica — the newest version, for a forward
/// deploy. A worker handed a step its own manifest does not contain uses <see cref="Matches"/> to decide
/// whether it is stale (the stored manifest differs: another version is live, so the step may belong to
/// it) or current (the step was genuinely removed).
/// </para>
/// <para>
/// One writer, one reader: the sync service and the engine both go through <see cref="Serialize"/>, so
/// the serializer options cannot drift between the two.
/// </para>
/// </remarks>
internal static class RegisteredManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Keyed on the definition instance: a registered manifest never changes after startup, so the
    // serialized form is computed at most once per flow per process, and only on the rare path that needs it.
    private static readonly ConditionalWeakTable<IFlowDefinition, string> Cache = new();

    /// <summary>Serializes <paramref name="flow"/>'s manifest exactly as it is persisted in the flow store.</summary>
    /// <param name="flow">The flow whose manifest is serialized.</param>
    /// <returns>The manifest JSON, cached per definition instance.</returns>
    public static string Serialize(IFlowDefinition flow)
        => Cache.GetValue(flow, static f => JsonSerializer.Serialize(f.Manifest, JsonOptions));

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="storedManifestJson"/> describes the same manifest
    /// as <paramref name="flow"/> — i.e. this worker holds the registered version.
    /// </summary>
    /// <param name="storedManifestJson">The manifest JSON read from the flow store.</param>
    /// <param name="flow">The flow definition this worker holds.</param>
    /// <remarks>
    /// An ordinal string match answers the common case without parsing. Otherwise both documents are
    /// compared structurally, so a store that normalises JSON (whitespace, property order) does not make a
    /// current worker look stale. A stored value that is not valid JSON never matches. O(manifest size),
    /// and only on the unknown-step path — never per step completion.
    /// </remarks>
    public static bool Matches(string storedManifestJson, IFlowDefinition flow)
    {
        var own = Serialize(flow);
        if (string.Equals(storedManifestJson, own, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            using var stored = JsonDocument.Parse(storedManifestJson);
            using var current = JsonDocument.Parse(own);
            return JsonElement.DeepEquals(stored.RootElement, current.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
