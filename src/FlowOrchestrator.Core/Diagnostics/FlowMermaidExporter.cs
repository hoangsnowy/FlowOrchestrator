using System.Text;
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Diagnostics;

/// <summary>
/// Converts a <see cref="FlowManifest"/> into a Mermaid <c>flowchart</c> definition.
/// The returned string can be embedded in any Markdown surface that renders Mermaid
/// (GitHub README, Confluence, Notion, dev.to, …) without requiring a running app.
/// </summary>
public static class FlowMermaidExporter
{
    /// <summary>
    /// Generates a Mermaid flowchart for the given flow definition.
    /// </summary>
    /// <param name="flow">The flow whose <see cref="IFlowDefinition.Manifest"/> will be rendered.</param>
    /// <param name="options">Optional rendering options; falls back to defaults when <see langword="null"/>.</param>
    /// <returns>A Mermaid <c>flowchart</c> string suitable for direct Markdown embedding.</returns>
    public static string ToMermaid(this IFlowDefinition flow, MermaidExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return ToMermaid(flow.Manifest, options);
    }

    /// <summary>
    /// Generates a Mermaid flowchart for the given manifest.
    /// </summary>
    /// <param name="manifest">The manifest to render.</param>
    /// <param name="options">Optional rendering options; falls back to defaults when <see langword="null"/>.</param>
    /// <returns>A Mermaid <c>flowchart</c> string suitable for direct Markdown embedding.</returns>
    public static string ToMermaid(this FlowManifest manifest, MermaidExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var opts = options ?? new MermaidExportOptions();
        var ids = NodeIds.For(manifest, opts.IncludeTriggers);

        var sb = new StringBuilder();
        sb.Append("flowchart ").AppendLine(opts.Direction);

        if (opts.ApplyStyling)
        {
            sb.AppendLine("    classDef trigger fill:#e1f5ff,stroke:#0288d1");
            sb.AppendLine("    classDef entry fill:#c8e6c9,stroke:#388e3c");
            sb.AppendLine("    classDef polling fill:#fff9c4,stroke:#f57f17");
            sb.AppendLine("    classDef loop fill:#f3e5f5,stroke:#7b1fa2");
            sb.AppendLine();
        }

        if (opts.IncludeTriggers && manifest.Triggers.Count > 0)
        {
            EmitTriggerNodes(sb, manifest, opts, ids);
            sb.AppendLine();
        }

        EmitStepNodes(sb, manifest.Steps, opts, ids);

        if (opts.IncludeTriggers && manifest.Triggers.Count > 0)
        {
            sb.AppendLine();
            EmitTriggerEdges(sb, manifest, ids);
        }

        sb.AppendLine();
        EmitStepEdges(sb, manifest.Steps, ids);

        return sb.ToString().TrimEnd() + "\n";
    }

    private static void EmitTriggerNodes(StringBuilder sb, FlowManifest manifest, MermaidExportOptions opts, NodeIds ids)
    {
        foreach (var (name, trigger) in manifest.Triggers)
        {
            var id = ids.Trigger(name);
            var label = BuildTriggerLabel(name, trigger);
            sb.Append("    ").Append(id).Append("[\"").Append(label).Append("\"]");
            if (opts.ApplyStyling)
            {
                sb.Append(":::trigger");
            }
            sb.AppendLine();
        }
    }

    private static void EmitStepNodes(StringBuilder sb, StepCollection steps, MermaidExportOptions opts, NodeIds ids)
    {
        foreach (var (key, metadata) in steps)
        {
            if (metadata is LoopStepMetadata loop)
            {
                EmitLoopSubgraph(sb, key, loop, opts, ids);
                continue;
            }

            EmitStepNode(sb, ids.Step(key), key, metadata, opts, indent: "    ");
        }
    }

    private static void EmitStepNode(
        StringBuilder sb,
        string id,
        string key,
        StepMetadata metadata,
        MermaidExportOptions opts,
        string indent)
    {
        var label = BuildStepLabel(key, metadata, opts);
        sb.Append(indent).Append(id).Append("[\"").Append(label).Append("\"]");

        if (opts.ApplyStyling)
        {
            var className = ResolveStepClass(metadata);
            if (className is not null)
            {
                sb.Append(":::").Append(className);
            }
        }

        sb.AppendLine();
    }

    private static void EmitLoopSubgraph(
        StringBuilder sb,
        string key,
        LoopStepMetadata loop,
        MermaidExportOptions opts,
        NodeIds ids)
    {
        var id = ids.Step(key);
        var subgraphId = "subgraph_" + id;
        var titleType = string.IsNullOrEmpty(loop.Type) ? "ForEach" : EscapeLabel(loop.Type);
        var title = $"🔁 {EscapeLabel(key)} ({titleType})";

        // The subgraph header itself stands in for the loop step in edges,
        // but we ALSO emit the loop's "outer" node id so callers can wire RunAfter
        // edges to the loop's key directly.
        sb.Append("    ").Append(id).Append("[\"").Append(BuildStepLabel(key, loop, opts)).Append("\"]");
        if (opts.ApplyStyling)
        {
            sb.Append(":::loop");
        }
        sb.AppendLine();

        sb.Append("    subgraph ").Append(subgraphId).Append("[\"").Append(title).Append("\"]").AppendLine();
        foreach (var (childKey, childMeta) in loop.Steps)
        {
            EmitStepNode(sb, ids.Child(key, childKey), childKey, childMeta, opts, indent: "        ");
        }
        sb.AppendLine("    end");

        // Wire the loop entry node into its subgraph for visual grouping.
        sb.Append("    ").Append(id).Append(" --> ").Append(subgraphId).AppendLine();
    }

    private static void EmitTriggerEdges(StringBuilder sb, FlowManifest manifest, NodeIds ids)
    {
        var entrySteps = manifest.Steps
            .Where(kvp => kvp.Value.RunAfter.Count == 0)
            .Select(kvp => kvp.Key)
            .ToArray();

        if (entrySteps.Length == 0)
        {
            return;
        }

        foreach (var triggerId in manifest.Triggers.Keys.Select(ids.Trigger))
        {
            foreach (var entry in entrySteps)
            {
                sb.Append("    ").Append(triggerId).Append(" --> ").Append(ids.Step(entry)).AppendLine();
            }
        }
    }

    private static void EmitStepEdges(StringBuilder sb, StepCollection steps, NodeIds ids)
    {
        foreach (var (key, metadata) in steps)
        {
            foreach (var (predecessorKey, condition) in metadata.RunAfter)
            {
                if (condition is null)
                {
                    continue;
                }

                var statuses = condition.Statuses;
                var labelParts = new List<string>(2);
                if (statuses is { Length: > 0 })
                {
                    labelParts.Add(string.Join('|', statuses.Select(s => s.ToString())));
                }
                if (!string.IsNullOrWhiteSpace(condition.When))
                {
                    labelParts.Add($"when: {EscapeLabel(condition.When!)}");
                }

                if (labelParts.Count == 0)
                {
                    continue;
                }

                sb.Append("    ")
                  .Append(ids.Step(predecessorKey))
                  .Append(" -- ").Append(string.Join(' ', labelParts)).Append(" --> ")
                  .Append(ids.Step(key))
                  .AppendLine();
            }
        }
    }

    private static string BuildTriggerLabel(string name, TriggerMetadata trigger)
    {
        var detail = trigger.Type switch
        {
            TriggerType.Manual => "Manual",
            TriggerType.Cron => trigger.Inputs.TryGetValue("cronExpression", out var cron) && cron is not null
                ? $"Cron {EscapeLabel(cron.ToString() ?? string.Empty)}"
                : "Cron",
            TriggerType.Webhook => trigger.Inputs.TryGetValue("webhookSlug", out var slug) && slug is not null
                ? $"Webhook /{EscapeLabel(slug.ToString() ?? string.Empty)}"
                : "Webhook",
            _ => trigger.Type.ToString()
        };

        return $"⚡ {EscapeLabel(name)}<br/>{detail}";
    }

    private static string BuildStepLabel(string key, StepMetadata metadata, MermaidExportOptions opts)
    {
        if (!opts.ShowStepTypes || string.IsNullOrEmpty(metadata.Type))
        {
            return EscapeLabel(key);
        }

        return $"{EscapeLabel(key)}<br/><i>{EscapeLabel(metadata.Type)}</i>";
    }

    private static string? ResolveStepClass(StepMetadata metadata)
    {
        if (metadata is LoopStepMetadata)
        {
            return "loop";
        }

        if (IsPollingStep(metadata))
        {
            return "polling";
        }

        if (metadata.RunAfter.Count == 0)
        {
            return "entry";
        }

        return null;
    }

    private static bool IsPollingStep(StepMetadata metadata)
    {
        if (!metadata.Inputs.TryGetValue("pollEnabled", out var value) || value is null)
        {
            return false;
        }

        return value switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var parsed) && parsed,
            _ => false
        };
    }

    private static string SafeId(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (var ch in key)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        // Mermaid identifiers must start with a letter; prefix if numeric.
        if (sb.Length == 0 || char.IsDigit(sb[0]))
        {
            sb.Insert(0, '_');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Assigns every node of one export a Mermaid id that no other node shares.
    /// </summary>
    /// <remarks>
    /// <see cref="SafeId"/> alone is lossy: <c>fetch-data</c> and <c>fetch_data</c> both become
    /// <c>fetch_data</c>, and a child key repeated in two loops (or matching a top-level key) would be one
    /// node — Mermaid then draws a single box carrying the edges of both. Ids are handed out in emission
    /// order: the first node with a given sanitized id keeps it, so a manifest without collisions renders
    /// exactly as before. A later loop child is qualified with its loop (<c>refunds__work</c>); any other
    /// later node gets a stable FNV-1a suffix of its raw key (<c>fetch_data_1a2b3c4d</c>), and a counter
    /// settles the (theoretical) case where that still collides.
    /// </remarks>
    private sealed class NodeIds
    {
        private readonly Dictionary<string, string> _triggers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _steps = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Loop, string Child), string> _children = new();
        private readonly HashSet<string> _taken = new(StringComparer.Ordinal);

        public static NodeIds For(FlowManifest manifest, bool includeTriggers)
        {
            var ids = new NodeIds();
            if (includeTriggers)
            {
                foreach (var name in manifest.Triggers.Keys)
                {
                    ids._triggers[name] = ids.Claim("T_" + SafeId(name), "trigger:" + name, qualified: null);
                }
            }

            foreach (var (key, metadata) in manifest.Steps)
            {
                ids._steps[key] = ids.Claim(SafeId(key), key, qualified: null);
                if (metadata is LoopStepMetadata loop)
                {
                    foreach (var childKey in loop.Steps.Keys)
                    {
                        ids._children[(key, childKey)] = ids.Claim(
                            SafeId(childKey),
                            key + "." + childKey,
                            qualified: SafeId(key) + "__" + SafeId(childKey));
                    }
                }
            }

            return ids;
        }

        public string Trigger(string name) => _triggers.TryGetValue(name, out var id) ? id : "T_" + SafeId(name);

        /// <summary>Id of a top-level step; a key the manifest does not declare (a dangling RunAfter) keeps its sanitized form.</summary>
        public string Step(string key) => _steps.TryGetValue(key, out var id) ? id : SafeId(key);

        public string Child(string loopKey, string childKey) => _children[(loopKey, childKey)];

        private string Claim(string preferred, string rawPath, string? qualified)
        {
            if (_taken.Add(preferred))
            {
                return preferred;
            }

            if (qualified is not null && _taken.Add(qualified))
            {
                return qualified;
            }

            var hashed = (qualified ?? preferred) + "_" + Fnv1a(rawPath).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
            var candidate = hashed;
            for (var n = 2; !_taken.Add(candidate); n++)
            {
                candidate = hashed + "_" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return candidate;
        }

        /// <summary>32-bit FNV-1a over the UTF-16 code units: stable across processes, unlike <see cref="string.GetHashCode()"/>.</summary>
        private static uint Fnv1a(string value)
        {
            var hash = 2166136261u;
            foreach (var ch in value)
            {
                hash = (hash ^ ch) * 16777619u;
            }

            return hash;
        }
    }

    private static string EscapeLabel(string value)
    {
        // Inside a "..."-wrapped Mermaid label, " breaks the parser. Use Mermaid's
        // HTML-style escape so the character still renders. Pipes and angle
        // brackets are left as-is since labels support a small HTML subset.
        return value.Replace("\"", "#quot;");
    }
}
