namespace Privon.App;

/// <summary>
/// Phase 3C STEP30/31 -- point-in-time, metadata-only evidence of one
/// <see cref="ClipboardComposerVerifier.VerifyAsync"/> attempt. Carries <see cref="Outcome"/> only
/// -- never the expected protected text, the composer text actually read, the generation checked,
/// the target used, or any hash/revision value.
///
/// STATE_VS_EVIDENCE_MODEL (Phase 3C STEP30.1, frozen): this is deliberately NOT
/// <see cref="Privon.Core.ProtectionState"/> and does not itself install any durable "currently
/// Verified" state anywhere. <see cref="Outcome"/> == <see cref="VerificationOutcome.Verified"/>
/// means only "at this one instant, the pinned pending attempt's expected protected text was
/// observed, exactly, in the focused authorized composer" -- never that the composer remains
/// matched afterward, that Send is authorized, that the message was safely sent, that the response
/// is protected, or any other standing guarantee. A future <c>ProtectionState</c> aggregator (not
/// implemented anywhere yet) would consume this evidence, generation-gated at render/consumption
/// time -- that reconciliation is out of scope here.
///
/// RESULT_DIAGNOSTICS: <see cref="ToString"/> exposes <see cref="Outcome"/> only.
/// </summary>
internal readonly record struct ComposerVerificationResult(VerificationOutcome Outcome)
{
    public override string ToString() => $"{nameof(ComposerVerificationResult)} {{ {nameof(Outcome)} = {Outcome} }}";
}
