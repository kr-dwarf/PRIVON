namespace Privon.App;

/// <summary>
/// Phase 3B STEP4 -- metadata-only outcome of one <see cref="IClipboardPrivacyProcessor.Process"/>
/// call. Deliberately carries only counts -- no raw text, no
/// <c>ClipboardTextSnapshot</c>, and no <c>DetectionResult</c>/<c>DetectionCandidate</c>/
/// <c>CanonicalValue</c>/<c>EvaluatedCandidate</c>/<c>TrustExceptionSnapshot</c> reference of any
/// kind, so it structurally cannot leak sensitive content through any observable surface
/// (ToString, logging, a future accidental field addition) -- defense in depth on top of, not
/// reliance on, the diagnostic hardening those Detection/App-owned types already got in Phase 3B
/// STEP3.1/STEP5.1.
///
/// <see cref="CandidateCount"/> == 0 means detection completed successfully and found nothing to
/// protect -- it does NOT mean detection failed. An execution failure (e.g. a detector throwing)
/// never produces this type at all: it propagates as an exception instead (see
/// <see cref="ClipboardPrivacyProcessor"/>'s own DETECTION_FAILURE_POLICY doc). The only
/// production caller always constructs this from <c>DetectionResult.Candidates.Count</c>, an
/// <c>IReadOnlyList&lt;T&gt;.Count</c> that is inherently non-negative -- no separate runtime
/// guard is added here for a value shape that cannot otherwise occur.
///
/// Phase 3B STEP4.1 (APP_DETECTION_METADATA_RESULT correction): internal, matching
/// <see cref="IClipboardPrivacyProcessor"/> and <see cref="ClipboardPrivacyProcessor"/> -- this
/// is pre-release intermediate orchestration state with no legitimate external consumer, only
/// ever produced and consumed inside <c>Privon.App</c>. Keeping it public would needlessly widen
/// this assembly's public surface and freeze a shape likely to change once policy integration
/// (a future STEP) extends what a processing attempt reports.
///
/// Phase 3B STEP8 (APP_TRUST_EVALUATION_METADATA): <see cref="TrustedCount"/> added -- the
/// number of detected candidates <see cref="Privon.Detection.ExceptionTrustedEvaluator"/> marked
/// <c>TrustState.Trusted</c>. Always 0 alongside <see cref="CandidateCount"/> == 0 (trust
/// evaluation never runs when nothing was detected -- see
/// <see cref="ClipboardPrivacyProcessor"/>'s own NO_PII fast path). No separate
/// "EvaluatedCandidateCount" field exists -- <c>ExceptionTrustedEvaluator.Evaluate</c>'s own
/// contract already guarantees candidate count/order are preserved exactly, so an evaluated count
/// would always just equal <see cref="CandidateCount"/>.
///
/// Phase 3B STEP10 (APP_BASE_POLICY_METADATA): <see cref="ProtectCount"/>/
/// <see cref="NeedsDecisionCount"/>/<see cref="BypassCount"/> added -- the number of
/// <c>CandidatePolicyDecision</c>s the real <c>Privon.Detection.CandidatePolicyEvaluator</c>
/// assigned each <c>CandidateDisposition</c>. <c>ProtectCount + NeedsDecisionCount + BypassCount
/// == CandidateCount</c> always (guaranteed by <c>CandidatePolicyEvaluator</c>'s own count/order
/// preservation, the same way <see cref="TrustedCount"/> relies on
/// <c>ExceptionTrustedEvaluator</c>'s). <see cref="TrustedCount"/> stays independently meaningful
/// -- it is NOT redundant with these three: base policy's <c>Bypass</c> disposition has two
/// distinct causes (a Trusted candidate, or a Level1/Low-confidence non-Trusted candidate), so
/// <see cref="BypassCount"/> can legitimately exceed <see cref="TrustedCount"/>. These three
/// counts describe BASE policy only (see <see cref="ClipboardPrivacyProcessor"/>'s own
/// APP_BASE_POLICY_BOUNDARY doc) -- none of them means "already aliased," "already replaced," or
/// "safe to send."
/// </summary>
internal readonly record struct ClipboardPrivacyProcessingResult(
    int CandidateCount,
    int TrustedCount,
    int ProtectCount,
    int NeedsDecisionCount,
    int BypassCount)
{
    public bool HasDetectedCandidates => CandidateCount > 0;

    /// <summary>Derived, never independently tracked -- always <see cref="CandidateCount"/> minus
    /// <see cref="TrustedCount"/>.</summary>
    public int UntrustedCount => CandidateCount - TrustedCount;
}
