using Privon.Core;
using Privon.Detection;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP25 -- the minimal immutable identity of one scope-bound, one-time bypass
/// authorization. 0.4.0 (D3A POL-L1-ONCE-R1/R2) widens the frozen identity by exactly one value: it is now
/// <see cref="Revision"/> + <see cref="DetectorId"/> + <see cref="Canonical"/>, nothing else -- a pass authorizes this canonical value as
/// produced by THIS detector at THIS revision, so the same canonical reported by another detector is not covered. The grant is only ever
/// minted for an item that carries a detector id, and it is consumed (atomically, at most once) by
/// <see cref="ClipboardDecisionScope.TryConsumeBypassGrant"/>. Deliberately excludes <c>RiskLevel</c> (not part of the frozen identity -- a
/// grant authorizes raw use of this exact canonical value at this exact revision, never
/// conditioned on which RiskLevel classification originally required a decision -- see the
/// Phase 3B STEP24 audit's GRANT_MULTIPLICITY finding), <c>Generation</c> (an App-level
/// lifecycle-isolation concept, never a grant key), any scope reference, raw text, target,
/// sequence, or consumed-state (consumption REMOVES the grant from the scope's resolution state; the grant itself carries
/// no consumed flag -- a grant that exists is a grant that has not been used).
///
/// A plain <c>readonly record struct</c> -- gives free structural equality/hashing so a set of
/// these can be deduplicated with an ordinary <see cref="HashSet{T}"/>, matching the exact same
/// shape precedent already established for <c>CanonicalValue</c>/<see cref="RevisionStamp"/>/
/// <c>ClipboardDispatchItem</c> themselves.
///
/// GRANT_DIAGNOSTICS: <see cref="ToString"/> is explicitly overridden and self-contained -- it
/// reads <see cref="Canonical"/>.<c>PiiType</c> and <see cref="Revision"/>.<c>RevisionId</c>
/// directly rather than delegating to either wrapped type's own (already-hardened) <c>ToString()</c>,
/// matching the established "never rely transitively on a wrapped type's continued safety"
/// discipline (<c>ClipboardDecisionItem</c>/<c>CandidatePolicyDecision</c>/<c>AliasAssignment</c>'s
/// own precedent). Never exposes the canonical value's own content or the revision's
/// <c>SnapshotHash</c>.
/// </summary>
internal readonly record struct ClipboardBypassGrant(RevisionStamp Revision, DetectorId DetectorId, CanonicalValue Canonical)
{
    public override string ToString() =>
        $"{nameof(ClipboardBypassGrant)} {{ PiiType = {Canonical.PiiType}, RevisionId = {Revision.RevisionId} }}";  // never the detector id or the value
}
