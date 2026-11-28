using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.HealthChecks;
using FlowOrchestrator.Core.Hosting;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Serialization;
using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.Core.Tests.Configuration;

/// <summary>
/// Locks the 2.0 public surface of <c>FlowOrchestrator.Core</c> and <c>FlowOrchestrator.InMemory</c>:
/// implementation types are <see langword="internal"/>, so a later 2.x can change them without a major.
/// Removing a public type after GA is a breaking change; making one of these public again must be a
/// deliberate API decision, not an accident this test lets through.
/// </summary>
public sealed class ImplementationTypeVisibilityTests
{
    /// <summary>Types reached only through <c>AddFlowOrchestrator()</c> and the public interfaces.</summary>
    public static TheoryData<Type> InternalTypes => new()
    {
        typeof(FlowOrchestratorEngine),
        typeof(IFlowGraphPlanner),
        typeof(FlowGraphPlanner),
        typeof(FlowGraphEvaluation),
        typeof(FlowGraphValidationResult),
        typeof(IStepExecutor),
        typeof(DefaultStepExecutor),
        typeof(IRunTimeoutEnforcer),
        typeof(StepLeaseKeeper),
        typeof(ExecutionContextAccessor),
        typeof(FlowSignalDispatcher),
        typeof(RecurringTriggerSync),
        typeof(FlowRunRecoveryHostedService),
        typeof(FlowStepLeaseHostedService),
        typeof(FlowTimeoutEnforcementHostedService),
        typeof(FlowUnsentDispatchRecoveryHostedService),
        typeof(FlowStoreHealthCheck),
        typeof(BooleanExpressionEvaluator),
        typeof(WhenClauseEvaluator),
        typeof(WhenEvaluationTrace),
        typeof(StepOutputResolver),
        typeof(EngineLogScope),
        typeof(RunAfterCollectionJsonConverter),
        typeof(RunAfterConditionJsonConverter),
        typeof(StepMetadataJsonConverter),
        typeof(MetadataInputExtensions),
        typeof(ActivityExtensions),
        typeof(InMemoryFlowRunStore),
        typeof(InMemoryFlowSignalStore),
        typeof(InMemoryFlowStore),
        typeof(InMemoryOutputsRepository),
    };

    /// <summary>The public contracts consumers inject in place of the internal implementations.</summary>
    public static TheoryData<Type> PublicContracts => new()
    {
        typeof(IFlowOrchestrator),
        typeof(IExecutionContextAccessor),
        typeof(IFlowSignalDispatcher),
        typeof(IRecurringTriggerSync),
        typeof(IStepHandler),
        typeof(ForEachStepHandler),
    };

    /// <summary>No implementation type is visible outside its assembly.</summary>
    /// <param name="type">The implementation type.</param>
    [Theory]
    [MemberData(nameof(InternalTypes))]
    public void Implementation_type_is_not_public(Type type)
    {
        // Arrange
        var name = type.FullName;

        // Act
        var visible = type.IsVisible;

        // Assert
        Assert.False(visible, $"{name} is public; it was made internal for 2.0.");
    }

    /// <summary>The supported replacements stay public.</summary>
    /// <param name="type">The public contract.</param>
    [Theory]
    [MemberData(nameof(PublicContracts))]
    public void Supported_contract_stays_public(Type type)
    {
        // Arrange
        var name = type.FullName;

        // Act
        var visible = type.IsVisible;

        // Assert
        Assert.True(visible, $"{name} must stay public: it is the supported route to an internal implementation.");
    }
}
