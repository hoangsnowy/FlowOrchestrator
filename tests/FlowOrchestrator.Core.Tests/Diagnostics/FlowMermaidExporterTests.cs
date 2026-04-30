using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Diagnostics;

namespace FlowOrchestrator.Core.Tests.Diagnostics;

public class FlowMermaidExporterTests
{
    [Fact]
    public void Linear_three_steps_emits_two_edges()
    {
        // Arrange
        var manifest = new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
            },
            Steps = new StepCollection
            {
                ["a"] = new StepMetadata { Type = "TypeA" },
                ["b"] = new StepMetadata
                {
                    Type = "TypeB",
                    RunAfter = new RunAfterCollection { ["a"] = new[] { StepStatus.Succeeded } }
                },
                ["c"] = new StepMetadata
                {
                    Type = "TypeC",
                    RunAfter = new RunAfterCollection { ["b"] = new[] { StepStatus.Succeeded } }
                }
            }
        };

        // Act
        var result = manifest.ToMermaid();

        // Assert
        Assert.Contains("flowchart TD", result);
        Assert.Contains("a -- Succeeded --> b", result);
        Assert.Contains("b -- Succeeded --> c", result);
    }

    [Fact]
    public void Header_respects_Direction_option()
    {
        // Arrange
        var manifest = new FlowManifest
        {
            Steps = new StepCollection { ["a"] = new StepMetadata { Type = "TypeA" } }
        };

        // Act
        var result = manifest.ToMermaid(new MermaidExportOptions { Direction = "LR" });

        // Assert
        Assert.Contains("flowchart LR", result);
    }

    [Fact]
    public void FanOut_emits_one_edge_per_dependent_step()
    {
        // Arrange
        var manifest = new FlowManifest
        {
            Steps = new StepCollection
            {
                ["root"] = new StepMetadata { Type = "Root" },
                ["a"] = new StepMetadata
                {
                    Type = "A",
                    RunAfter = new RunAfterCollection { ["root"] = new[] { StepStatus.Succeeded } }
                },
                ["b"] = new StepMetadata
                {
                    Type = "B",
                    RunAfter = new RunAfterCollection { ["root"] = new[] { StepStatus.Succeeded } }
                },
                ["c"] = new StepMetadata
                {
                    Type = "C",
                    RunAfter = new RunAfterCollection { ["root"] = new[] { StepStatus.Succeeded } }
                }
            }
        };

        // Act
        var result = manifest.ToMermaid();

        // Assert
        Assert.Contains("root -- Succeeded --> a", result);
        Assert.Contains("root -- Succeeded --> b", result);
        Assert.Contains("root -- Succeeded --> c", result);
    }

    [Fact]
    public void FanIn_emits_one_edge_from_each_predecessor_to_join()
    {
        // Arrange
        var manifest = new FlowManifest
        {
            Steps = new StepCollection
            {
                ["a"] = new StepMetadata { Type = "A" },
                ["b"] = new StepMetadata { Type = "B" },
                ["c"] = new StepMetadata { Type = "C" },
                ["join"] = new StepMetadata
                {
                    Type = "Join",
                    RunAfter = new RunAfterCollection
                    {
                        ["a"] = new[] { StepStatus.Succeeded },
                        ["b"] = new[] { StepStatus.Succeeded },
                        ["c"] = new[] { StepStatus.Succeeded }
                    }
                }
            }
        };

        // Act
        var result = manifest.ToMermaid();

        // Assert
        Assert.Contains("a -- Succeeded --> join", result);
        Assert.Contains("b -- Succeeded --> join", result);
        Assert.Contains("c -- Succeeded --> join", result);
    }

    [Fact]
    public void MultipleTriggers_emit_one_node_per_trigger_each_pointing_to_entry_steps()
    {
        // Arrange
        var manifest = new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["manual"] = new TriggerMetadata { Type = TriggerType.Manual },
                ["webhook"] = new TriggerMetadata
                {
                    Type = TriggerType.Webhook,
                    Inputs = new Dictionary<string, object?> { ["webhookSlug"] = "order-fulfillment" }
                }
            },
            Steps = new StepCollection
            {
                ["fetch"] = new StepMetadata { Type = "Fetch" }
            }
        };

        // Act
        var result = manifest.ToMermaid();

        // Assert
        Assert.Contains("T_manual", result);
        Assert.Contains("T_webhook", result);
        Assert.Contains("T_manual --> fetch", result);
        Assert.Contains("T_webhook --> fetch", result);
        Assert.Contains("order-fulfillment", result);
    }

    [Fact]
    public void LoopStep_emits_subgraph_with_child_steps()
    {
        // Arrange
        var manifest = new FlowManifest
        {
            Steps = new StepCollection
            {
                ["fetch"] = new StepMetadata { Type = "Fetch" },
                ["process_each"] = new LoopStepMetadata
                {
                    Type = "ForEach",
                    ForEach = "@triggerBody()?.items",
                    RunAfter = new RunAfterCollection { ["fetch"] = new[] { StepStatus.Succeeded } },
                    Steps = new StepCollection
                    {
                        ["validate"] = new StepMetadata { Type = "Validate" }
                    }
                }
            }
        };

        // Act
        var result = manifest.ToMermaid();

        // Assert
        Assert.Contains("subgraph", result);
        Assert.Contains("process_each", result);
        Assert.Contains("validate", result);
        Assert.Contains("end", result);
    }

    [Fact]
    public void RunAfter_with_multiple_statuses_emits_combined_label()
    {
        // Arrange
        var manifest = new FlowManifest
        {
            Steps = new StepCollection
            {
                ["upstream"] = new StepMetadata { Type = "Upstream" },
                ["downstream"] = new StepMetadata
                {
                    Type = "Down",
                    RunAfter = new RunAfterCollection
                    {
                        ["upstream"] = new[] { StepStatus.Succeeded, StepStatus.Failed }
                    }
                }
            }
        };

        // Act
        var result = manifest.ToMermaid();

        // Assert
        // Combined label: e.g. "upstream -- Succeeded|Failed --> downstream"
        Assert.Matches(@"upstream -- Succeeded\|Failed --> downstream", result);
    }

    [Fact]
    public void StepKey_with_hyphens_uses_safe_node_id_but_preserves_label()
    {
        // Arrange
        var manifest = new FlowManifest
        {
            Steps = new StepCollection
            {
                ["my-step"] = new StepMetadata { Type = "TypeA" },
                ["next"] = new StepMetadata
                {
                    Type = "TypeB",
                    RunAfter = new RunAfterCollection { ["my-step"] = new[] { StepStatus.Succeeded } }
                }
            }
        };

        // Act
        var result = manifest.ToMermaid();

        // Assert
        // Safe ID for "my-step" must not contain a hyphen — Mermaid treats it as a token.
        Assert.Contains("my_step", result);
        // Display label (inside quotes) must keep the original key.
        Assert.Contains("\"my-step", result);
        Assert.Contains("my_step -- Succeeded --> next", result);
    }

    [Fact]
    public void StepType_with_quote_is_escaped_in_label()
    {
        // Arrange
        var manifest = new FlowManifest
        {
            Steps = new StepCollection
            {
                ["a"] = new StepMetadata { Type = "Has\"Quote" }
            }
        };

        // Act
        var result = manifest.ToMermaid();

        // Assert
        // A bare " inside a Mermaid label breaks the parser.
        // Either escape with #quot; or strip — the only requirement is it must NOT be a raw " inside the label.
        Assert.DoesNotContain("Has\"Quote", result);
    }

    [Fact]
    public void Polling_step_gets_polling_class_when_styling_enabled()
    {
        // Arrange
        var manifest = new FlowManifest
        {
            Steps = new StepCollection
            {
                ["poll"] = new StepMetadata
                {
                    Type = "CallExternalApi",
                    Inputs = new Dictionary<string, object?> { ["pollEnabled"] = true }
                }
            }
        };

        // Act
        var result = manifest.ToMermaid();

        // Assert
        Assert.Contains(":::polling", result);
    }

    [Fact]
    public void Entry_step_gets_entry_class_when_styling_enabled()
    {
        // Arrange
        var manifest = new FlowManifest
        {
            Steps = new StepCollection
            {
                ["a"] = new StepMetadata { Type = "TypeA" }
            }
        };

        // Act
        var result = manifest.ToMermaid();

        // Assert
        Assert.Contains(":::entry", result);
    }

    [Fact]
    public void ApplyStyling_false_omits_classDef_lines()
    {
        // Arrange
        var manifest = new FlowManifest
        {
            Steps = new StepCollection
            {
                ["a"] = new StepMetadata { Type = "TypeA" }
            }
        };

        // Act
        var result = manifest.ToMermaid(new MermaidExportOptions { ApplyStyling = false });

        // Assert
        Assert.DoesNotContain("classDef", result);
        Assert.DoesNotContain(":::entry", result);
    }

    [Fact]
    public void IncludeTriggers_false_omits_trigger_nodes_and_edges()
    {
        // Arrange
        var manifest = new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
            },
            Steps = new StepCollection
            {
                ["a"] = new StepMetadata { Type = "TypeA" }
            }
        };

        // Act
        var result = manifest.ToMermaid(new MermaidExportOptions { IncludeTriggers = false });

        // Assert
        Assert.DoesNotContain("T_manual", result);
    }

    [Fact]
    public void ShowStepTypes_false_omits_type_label_text()
    {
        // Arrange
        var manifest = new FlowManifest
        {
            Steps = new StepCollection
            {
                ["a"] = new StepMetadata { Type = "TypeA" }
            }
        };

        // Act
        var result = manifest.ToMermaid(new MermaidExportOptions { ShowStepTypes = false });

        // Assert
        Assert.DoesNotContain("TypeA", result);
        Assert.DoesNotContain("<i>", result);
    }

    [Fact]
    public void IFlowDefinition_extension_delegates_to_manifest()
    {
        // Arrange
        var flow = new SampleFlow();

        // Act
        var fromFlow = flow.ToMermaid();
        var fromManifest = flow.Manifest.ToMermaid();

        // Assert
        Assert.Equal(fromManifest, fromFlow);
    }

    private sealed class SampleFlow : IFlowDefinition
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Version => "1.0";
        public FlowManifest Manifest { get; set; } = new FlowManifest
        {
            Steps = new StepCollection
            {
                ["a"] = new StepMetadata { Type = "TypeA" },
                ["b"] = new StepMetadata
                {
                    Type = "TypeB",
                    RunAfter = new RunAfterCollection { ["a"] = new[] { StepStatus.Succeeded } }
                }
            }
        };
    }
}
