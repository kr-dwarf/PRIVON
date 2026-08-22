namespace Privon.Core;

/// <summary>
/// Identifies exactly which editing state a prior validation/decision was made for. A
/// stored decision is reusable only when both the revision id and the snapshot hash match
/// the current state exactly.
///
/// REVISIONSTAMP_DIAGNOSTIC_SURFACE (Phase 3B STEP15.1): <see cref="ToString"/> is explicitly
/// overridden to project only <see cref="RevisionId"/> -- a plain monotonic counter already
/// classified as safe metadata (see <see cref="Privon.Core.RevisionId"/>'s own doc) -- and never
/// delegates to <see cref="Hash"/>'s own <c>ToString()</c> (self-contained hardening, not reliant
/// on <see cref="SnapshotHash"/>'s hardened override staying correct forever -- same defense-in-depth
/// discipline already used for <c>Privon.Detection.AliasAssignment</c>/<c>CandidatePolicyDecision</c>
/// projecting their wrapped types' safe fields directly rather than delegating).
/// </summary>
public readonly record struct RevisionStamp(RevisionId RevisionId, SnapshotHash Hash)
{
    public bool Matches(RevisionStamp current) => this == current;

    public override string ToString() => $"{nameof(RevisionStamp)} {{ RevisionId = {RevisionId} }}";
}
