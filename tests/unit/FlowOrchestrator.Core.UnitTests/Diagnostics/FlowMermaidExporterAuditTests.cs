using System.Text.RegularExpressions;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Diagnostics;

namespace FlowOrchestrator.Core.Tests.Diagnostics;

/// <summary>
/// Audit (4th sweep) — Mermaid export identity defects. <c>SafeId</c> maps every non
/// letter/digit/underscore character to <c>_</c> and never disambiguates, and loop children are
/// emitted under their bare child key, so distinct steps collapse into ONE Mermaid node and the
/// rendered DAG (dashboard "Mermaid" tab, README embeds) shows wrong edges. These tests failed on the
/// audited commit.
/// </summary>
public sealed class FlowMermaidExporterAuditTests
{
    private static readonly Regex NodeDeclaration = new(@"^\s+([A-Za-z0-9_]+)\[""", RegexOptions.Multiline);

    [Fact]
    public void Step_keys_differing_only_by_punctuation_get_distinct_node_ids()
    {
        // Arrange
        var manifest = new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = new StepCollection
            {
                ["fetch-data"] = new StepMetadata { Type = "Http" },
                ["fetch_data"] = new StepMetadata { Type = "Sql" },
            }
        };

        // Act
        var mermaid = manifest.ToMermaid();
        var stepNodeIds = NodeDeclaration.Matches(mermaid).Select(m => m.Groups[1].Value).Where(id => !id.StartsWith("T_", StringComparison.Ordinal)).ToList();

        // Assert — 2 distinct node ids, not "fetch_data" declared twice (one node).
        Assert.Equal(2, stepNodeIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Children_with_the_same_key_in_two_loops_get_distinct_node_ids()
    {
        // Arrange
        var manifest = new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = new StepCollection
            {
                ["orders"] = new LoopStepMetadata
                {
                    Type = "ForEach",
                    ForEach = "@triggerBody()?.orders",
                    Steps = new StepCollection { ["work"] = new StepMetadata { Type = "ProcessOrder" } }
                },
                ["refunds"] = new LoopStepMetadata
                {
                    Type = "ForEach",
                    ForEach = "@triggerBody()?.refunds",
                    RunAfter = new RunAfterCollection { ["orders"] = [StepStatus.Succeeded] },
                    Steps = new StepCollection { ["work"] = new StepMetadata { Type = "ProcessRefund" } }
                },
            }
        };

        // Act
        var mermaid = manifest.ToMermaid();
        var workDeclarations = NodeDeclaration.Matches(mermaid).Select(m => m.Groups[1].Value).Where(id => id.Contains("work", StringComparison.Ordinal)).ToList();

        // Assert — two distinct ids (one per loop), not "work" twice as one shared node.
        Assert.Equal(2, workDeclarations.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Edges_follow_the_disambiguated_ids_and_unique_keys_keep_their_plain_id()
    {
        // Arrange
        var manifest = new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = new StepCollection
            {
                ["fetch_data"] = new StepMetadata { Type = "Sql" },
                ["fetch-data"] = new StepMetadata { Type = "Http" },
                ["report"] = new StepMetadata
                {
                    Type = "Report",
                    RunAfter = new RunAfterCollection { ["fetch-data"] = [StepStatus.Succeeded] }
                },
            }
        };

        // Act
        var mermaid = manifest.ToMermaid();
        var httpNodeId = NodeDeclaration.Matches(mermaid)
            .Select(m => m.Groups[1].Value)
            .Single(id => id.StartsWith("fetch_data_", StringComparison.Ordinal));

        // Assert
        Assert.Contains("    fetch_data[\"", mermaid);
        Assert.Contains("    report[\"", mermaid);
        Assert.Contains($"{httpNodeId} -- Succeeded --> report", mermaid);
        Assert.DoesNotContain("fetch_data -- Succeeded --> report", mermaid);
        Assert.Equal(mermaid, manifest.ToMermaid());
    }
}
