namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Well-known reason strings persisted on <c>FlowSteps.ErrorMessage</c> when a step
/// is marked <see cref="Abstractions.StepStatus.Skipped"/>.
/// </summary>
/// <remarks>
/// These constants are part of the public storage contract — the engine writes them
/// at skip time and reads them back when reversing cascade-skips on retry.
/// External tooling that inspects step records can rely on the exact text.
/// </remarks>
public static class StepSkipReasons
{
    /// <summary>
    /// Recorded when a step's <c>runAfter</c> dependency reached a final status that
    /// the dependency clause did not accept (e.g. <c>RunAfter[B] = Succeeded</c> but
    /// B finished as <c>Failed</c>). Skips bearing this reason are reversible: a
    /// successful manual retry of the upstream step clears them so the downstream
    /// step can be re-evaluated.
    /// </summary>
    public const string PrerequisitesUnmet = "Prerequisite status did not satisfy runAfter conditions.";
}
