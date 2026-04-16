using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

/// <summary>
/// HelloWorldFlow — A minimal two-step flow that demonstrates the basics of FlowOrchestrator.
///
/// Use this as a starting point when exploring the library. It fires every minute via a cron
/// trigger (or manually from the dashboard) and logs two messages in sequence.
///
/// Steps:
///   system_check  → LogMessage — logs that OrderHub is starting up
///   system_ready  → LogMessage — logs that all systems are operational
/// </summary>
public sealed class HelloWorldFlow : IFlowDefinition
{
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000001");
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual },
            ["scheduled"] = new TriggerMetadata
            {
                Type = TriggerType.Cron,
                Inputs = new Dictionary<string, object?> { ["cronExpression"] = "*/1 * * * *" }
            }
        },
        Steps = new StepCollection
        {
            ["system_check"] = new StepMetadata
            {
                Type = "LogMessage",
                Inputs = new Dictionary<string, object?> { ["message"] = "OrderHub starting — running system health check..." }
            },
            ["system_ready"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection { ["system_check"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["message"] = "OrderHub is online — all systems operational." }
            }
        }
    };
}
