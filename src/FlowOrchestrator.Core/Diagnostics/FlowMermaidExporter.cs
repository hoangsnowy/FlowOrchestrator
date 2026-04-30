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
            EmitTriggerNodes(sb, manifest, opts);
            sb.AppendLine();
        }

        EmitStepNodes(sb, manifest.Steps, opts);

        if (opts.IncludeTriggers && manifest.Triggers.Count > 0)
        {
            sb.AppendLine();
            EmitTriggerEdges(sb, manifest);
        }

        sb.AppendLine();
        EmitStepEdges(sb, manifest.Steps);

        return sb.ToString().TrimEnd() + "\n";
    }

    private static void EmitTriggerNodes(StringBuilder sb, FlowManifest manifest, MermaidExportOptions opts)
    {
        foreach (var (name, trigger) in manifest.Triggers)
        {
            var id = "T_" + SafeId(name);
            var label = BuildTriggerLabel(name, trigger);
            sb.Append("    ").Append(id).Append("[\"").Append(label).Append("\"]");
            if (opts.ApplyStyling)
            {
                sb.Append(":::trigger");
            }
            sb.AppendLine();
        }
    }

    private static void EmitStepNodes(StringBuilder sb, StepCollection steps, MermaidExportOptions opts)
    {
        foreach (var (key, metadata) in steps)
        {
            if (metadata is LoopStepMetadata loop)
            {
                EmitLoopSubgraph(sb, key, loop, opts);
                continue;
            }

            EmitStepNode(sb, key, metadata, opts, indent: "    ");
        }
    }

    private static void EmitStepNode(
        StringBuilder sb,
        string key,
        StepMetadata metadata,
        MermaidExportOptions opts,
        string indent)
    {
        var id = SafeId(key);
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
        MermaidExportOptions opts)
    {
        var id = SafeId(key);
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
            EmitStepNode(sb, childKey, childMeta, opts, indent: "        ");
        }
        sb.AppendLine("    end");

        // Wire the loop entry node into its subgraph for visual grouping.
        sb.Append("    ").Append(id).Append(" --> ").Append(subgraphId).AppendLine();
    }

    private static void EmitTriggerEdges(StringBuilder sb, FlowManifest manifest)
    {
        var entrySteps = manifest.Steps
            .Where(kvp => kvp.Value.RunAfter.Count == 0)
            .Select(kvp => kvp.Key)
            .ToArray();

        if (entrySteps.Length == 0)
        {
            return;
        }

        foreach (var triggerName in manifest.Triggers.Keys)
        {
            var triggerId = "T_" + SafeId(triggerName);
            foreach (var entry in entrySteps)
            {
                sb.Append("    ").Append(triggerId).Append(" --> ").Append(SafeId(entry)).AppendLine();
            }
        }
    }

    private static void EmitStepEdges(StringBuilder sb, StepCollection steps)
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
                  .Append(SafeId(predecessorKey))
                  .Append(" -- ").Append(string.Join(' ', labelParts)).Append(" --> ")
                  .Append(SafeId(key))
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

    private static string EscapeLabel(string value)
    {
        // Inside a "..."-wrapped Mermaid label, " breaks the parser. Use Mermaid's
        // HTML-style escape so the character still renders. Pipes and angle
        // brackets are left as-is since labels support a small HTML subset.
        return value.Replace("\"", "#quot;");
    }
}
