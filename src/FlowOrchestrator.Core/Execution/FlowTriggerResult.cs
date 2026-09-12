using System.Text.Json.Serialization;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Outcome of <see cref="IFlowOrchestrator.TriggerAsync"/>.
/// </summary>
/// <param name="RunId">
/// The run that was started, or <see langword="null"/> when no run was created because the flow is
/// disabled.
/// </param>
/// <param name="Disabled">
/// <see langword="true"/> when the trigger was silently dropped because the flow is disabled. No run
/// exists and <paramref name="RunId"/> is <see langword="null"/>.
/// </param>
/// <param name="Duplicate">
/// <see langword="true"/> when an idempotency key matched an earlier trigger, in which case
/// <paramref name="RunId"/> is the run that already exists rather than a new one.
/// </param>
/// <remarks>
/// <para>
/// This replaces the anonymous objects the engine used to return through its <c>object?</c> contract.
/// The signature is unchanged, so nothing breaks, but callers can now actually read the outcome —
/// which matters because the two facts it carries are exactly the ones a caller must not assume.
/// </para>
/// <para>
/// Every trigger entry point is required to honour <see cref="Disabled"/>. Reporting a caller-side
/// run id without checking it hands back an identifier that no run backs: a subsequent read of that
/// run 404s, and the caller believes a disabled flow executed. The engine's own gate is not enough
/// on its own, because it can only refuse to do work — it cannot stop a caller from inventing a run
/// id and reporting success.
/// </para>
/// <para>
/// The <see cref="JsonPropertyNameAttribute"/> attributes are not decoration. The anonymous objects this
/// replaced serialised as <c>runId</c> / <c>disabled</c> / <c>duplicate</c>, and anything that wrote
/// the engine's return value straight onto the wire depended on that casing. Pinning the names keeps
/// those payloads byte-identical, so introducing the type is not a breaking change for them.
/// </para>
/// </remarks>
public sealed record FlowTriggerResult(
    [property: JsonPropertyName("runId")] Guid? RunId,
    [property: JsonPropertyName("disabled")] bool Disabled = false,
    [property: JsonPropertyName("duplicate")] bool Duplicate = false);
