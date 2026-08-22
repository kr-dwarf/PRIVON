using Privon.Core;
using Privon.Detection;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP19 -- the minimum identity a NeedsDecision candidate must carry from
/// <see cref="ClipboardPrivacyProcessor"/> into a <see cref="ClipboardDecisionPlan"/>. Exactly
/// two fields, frozen by the Phase 3B STEP18 audit's DECISION_ITEM_IDENTITY resolution:
///
/// - <see cref="Canonical"/>: the exact canonical identity a future runtime-decision/grant needs
///   (frozen grant key remains <c>RevisionStamp + CanonicalValue</c> -- this is the
///   <c>CanonicalValue</c> half). Already carries <c>PiiType</c> internally, so no separate
///   <c>PiiType</c> field is duplicated here.
/// - <see cref="RiskLevel"/>: the ONLY thing needed to distinguish a future Level1
///   three-choice decision flow from a Level3 second-confirmation flow. Required because
///   <c>CanonicalValue</c> alone cannot answer that -- <see cref="Privon.Detection.CandidatePolicyEvaluator"/>'s
///   own decision function produces <c>NeedsDecision</c> from exactly two, otherwise
///   indistinguishable, sources: Level3 (any Confidence/TrustState) or Level1 non-Trusted
///   Confidence.Medium.
///
/// Deliberately excluded (each proven either fully redundant or unneeded by the STEP18 audit,
/// not merely omitted for convenience):
/// - <c>DetectionConfidence</c>: for a NeedsDecision candidate it is either always exactly
///   <c>Medium</c> (Level1 -- the only Confidence that ever reaches NeedsDecision at that level)
///   or never consulted at all (Level3) -- it carries zero information beyond what
///   <see cref="RiskLevel"/> already implies.
/// - <c>Privon.Core.TrustState</c>: a NeedsDecision candidate is structurally always
///   <c>Untrusted</c> (Trusted candidates always resolve to <c>Bypass</c> before this decision is
///   even reached, and Level3 candidates are never evaluated against either trust list at all --
///   see <c>ExceptionTrustedEvaluator</c>'s own Level3 branch). A constant value carries no
///   information.
/// - <c>RawSpan</c>: immediately stale the instant text changes; a future decision-time
///   revalidation always re-reads and re-detects fresh rather than trusting a stored coordinate.
/// - Any raw/canonical string content directly, any <c>AliasToken</c>, any replacement text.
///
/// A plain <c>readonly record struct</c> -- small, copyable, value-semantic, and (critically)
/// gives free structural equality so a list of these can be deduplicated with an ordinary
/// <see cref="HashSet{T}"/> (see <see cref="ClipboardPrivacyProcessor"/>'s
/// DECISION_PLAN_DUPLICATE_POLICY implementation) -- matching the same shape precedent as
/// <c>CanonicalValue</c>/<c>Privon.Core.RevisionStamp</c>/<c>ClipboardDispatchItem</c>.
///
/// <see cref="ToString"/> is explicitly overridden and self-contained -- it reads
/// <see cref="Canonical"/>.<c>PiiType</c> directly rather than delegating to
/// <see cref="CanonicalValue"/>'s own (already-hardened) <c>ToString()</c>, matching the
/// established "never rely transitively on a wrapped type's continued safety" discipline
/// (<c>CandidatePolicyDecision</c>/<c>AliasAssignment</c>'s own precedent). Never exposes the
/// canonical value's own content, any raw text, span, hash, or fingerprint.
/// </summary>
internal readonly record struct ClipboardDecisionItem(CanonicalValue Canonical, RiskLevel RiskLevel)
{
    public override string ToString() =>
        $"{nameof(ClipboardDecisionItem)} {{ PiiType = {Canonical.PiiType}, {nameof(RiskLevel)} = {RiskLevel} }}";
}
