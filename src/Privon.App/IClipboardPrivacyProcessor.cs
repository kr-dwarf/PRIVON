using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP4 -- narrow App-owned seam over the real Detection pipeline, scoped to exactly
/// what <see cref="ClipboardPrivacyCoordinator"/> needs handed off after a successful guarded
/// read. Synchronous -- Detection is CPU-local work, not I/O, so wrapping it in <c>Task</c> here
/// would be an unnecessary abstraction (Phase 3B STEP3 audit's own PROCESSOR_API_RECOMMENDATION
/// finding).
///
/// This interface exists so App-level orchestration can be tested with a hand-written fake
/// instead of running the real <c>DetectionPipeline</c> on every coordinator-boundary test,
/// without widening <c>Privon.Detection</c>'s own public surface or reaching into it via
/// <c>InternalsVisibleTo</c> -- the same reasoning already used for
/// <see cref="IClipboardReadTransport"/> and <see cref="IForegroundTargetCapture"/>.
/// </summary>
internal interface IClipboardPrivacyProcessor
{
    /// <summary>
    /// Runs the real Detection -> Trust/Exception -> Base Policy -> Alias Assignment -> (when
    /// eligible) Alias Replacement chain against <paramref name="snapshot"/>'s text and returns
    /// the metadata-only <see cref="ClipboardPrivacyProcessingOutcome.Result"/> plus, exclusively,
    /// either a <see cref="ClipboardPrivacyProcessingOutcome.WritePlan"/> (Phase 3B STEP14 -- base
    /// policy fully resolved this attempt with something to protect) or a
    /// <see cref="ClipboardPrivacyProcessingOutcome.DecisionPlan"/> (Phase 3B STEP19 -- at least
    /// one candidate still needs a decision). This remains exactly one delegated call per
    /// notification (SUCCESS_HANDOFF_BOUNDARY) -- Detection is never re-run to obtain either plan
    /// separately. <paramref name="expectedTarget"/> is not consumed by Detection itself -- it
    /// travels through unchanged so the coordinator's own guarded write can forward the SAME token
    /// that authorized the guarded read, without needing to silently recapture a different one
    /// mid-composition (Phase 3B STEP3 audit's TARGET_TOKEN_LIFETIME finding, extended by the
    /// STEP13 audit's TARGET/SEQUENCE_TOKEN_FLOW).
    /// </summary>
    ClipboardPrivacyProcessingOutcome Process(ForegroundTargetSnapshot expectedTarget, ClipboardTextSnapshot snapshot);
}
