using Privon.Core;
using Privon.Detection;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP25 -- the minimal immutable identity of one scope-bound, one-time bypass
/// authorization: exactly the frozen grant identity (Phase 2W.1, reconfirmed by the Phase 3B
/// STEP24 audit's GRANT_IDENTITY finding) -- <see cref="Revision"/> + <see cref="Canonical"/>,
/// nothing else. Deliberately excludes <c>RiskLevel</c> (not part of the frozen identity -- a
/// grant authorizes raw use of this exact canonical value at this exact revision, never
/// conditioned on which RiskLevel classification originally required a decision -- see the
/// Phase 3B STEP24 audit's GRANT_MULTIPLICITY finding), <c>Generation</c> (an App-level
/// lifecycle-isolation concept, never a grant key), any scope reference, raw text, target,
/// sequence, or consumed-state (grant consumption is future work -- Phase 3B STEP22/24 audits'
/// SEND_CAPABILITY_LIMITATION finding; no Remove/Consume operation exists anywhere near this
/// type yet).
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
internal readonly record struct ClipboardBypassGrant(RevisionStamp Revision, CanonicalValue Canonical)
{
    public override string ToString() =>
        $"{nameof(ClipboardBypassGrant)} {{ PiiType = {Canonical.PiiType}, RevisionId = {Revision.RevisionId} }}";
}
