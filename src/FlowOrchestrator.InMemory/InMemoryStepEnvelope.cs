using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.InMemory;

/// <summary>
/// Lightweight in-process wrapper that captures everything the
/// <see cref="InMemoryStepRunnerHostedService"/> needs to call
/// <c>IFlowOrchestrator.RunStepAsync</c> after a step has been dispatched.
/// </summary>
/// <remarks>
/// All fields are live .NET references — no serialisation occurs.
/// This envelope is only valid within a single process lifetime.
/// </remarks>
internal sealed record InMemoryStepEnvelope(
    /// <summary>The execution context carrying RunId and trigger data.</summary>
    IExecutionContext Context,

    /// <summary>The flow definition the step belongs to.</summary>
    IFlowDefinition Flow,

    /// <summary>The step instance with resolved inputs.</summary>
    IStepInstance Step,

    /// <summary>Opaque identifier returned to the caller as the "job id".</summary>
    string EnvelopeId);
