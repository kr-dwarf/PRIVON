namespace Privon.App;

/// <summary>
/// Phase 3B STEP14 -- the full return of one <see cref="IClipboardPrivacyProcessor.Process"/>
/// call: the existing metadata-only <see cref="Result"/> (unchanged shape, Phase 3B
/// STEP4/STEP8/STEP10) plus an optional <see cref="WritePlan"/>, extended in Phase 3B STEP19 with
/// an optional <see cref="DecisionPlan"/>.
///
/// <see cref="WritePlan"/> is non-null only when base policy fully resolved this attempt (no
/// <c>NeedsDecision</c> candidates remain) AND at least one candidate needed masking
/// (<c>Result.ProtectCount &gt; 0</c>) -- see <see cref="ClipboardPrivacyProcessor"/>'s own
/// NEEDSDECISION_BLOCKS_REPLACEMENT / ALL_BYPASS doc for the exact gating. A non-null
/// <see cref="WritePlan"/> means only "the real <c>AliasReplacer</c> produced a rewritten
/// string, ready to hand to a guarded clipboard write" -- it does NOT mean the clipboard was
/// actually rewritten, that Send is safe, or that <c>Privon.Core.ProtectionState.Verified</c> was
/// reached. Whether the write is even attempted (its own <c>HasReliableSequence</c> gate) and
/// what its outcome means are entirely <see cref="ClipboardPrivacyCoordinator"/>'s concern.
///
/// <see cref="DecisionPlan"/> is non-null if and only if <c>Result.NeedsDecisionCount &gt; 0</c>
/// (PROCESSING_OUTCOME_PLAN_EXCLUSIVITY, Phase 3B STEP18 audit, frozen) -- this is guaranteed by
/// construction in <see cref="ClipboardPrivacyProcessor.Process"/> itself (the two conditions
/// that gate <see cref="WritePlan"/> vs <see cref="DecisionPlan"/> are already mutually exclusive
/// given how <c>ProtectCount</c>/<c>NeedsDecisionCount</c>/<c>BypassCount</c> partition
/// <c>CandidateCount</c>), so no separate runtime validation is added here for a shape the
/// processor cannot otherwise produce. A non-null <see cref="DecisionPlan"/> means only "these
/// canonical identities need a decision" -- it does NOT mean a decision session has been created,
/// published, or that any grant exists; see <see cref="ClipboardDecisionPlan"/>'s own doc.
///
/// <see cref="ToString"/> delegates to <see cref="Result"/>'s own safe (counts-only) rendering
/// and, for <see cref="WritePlan"/>/<see cref="DecisionPlan"/>, only ever reports whether each is
/// present -- never anything from inside either (see <see cref="ClipboardWritePlan"/>/
/// <see cref="ClipboardDecisionPlan"/>'s own diagnostic-hardened <c>ToString</c>).
/// </summary>
internal sealed record ClipboardPrivacyProcessingOutcome(
    ClipboardPrivacyProcessingResult Result, ClipboardWritePlan? WritePlan, ClipboardDecisionPlan? DecisionPlan)
{
    public override string ToString() =>
        $"{nameof(ClipboardPrivacyProcessingOutcome)} {{ Result = {Result}, HasWritePlan = {WritePlan is not null}, " +
        $"HasDecisionPlan = {DecisionPlan is not null} }}";
}
