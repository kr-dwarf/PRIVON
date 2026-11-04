using Privon.Core;

namespace Privon.Detection.Family;

/// <summary>D3B0-A A.9 §19 -- whether a family attempt was accepted or rejected.</summary>
public enum FamilyAttemptOutcome
{
    Accepted = 0,
    Rejected = 1,
}

/// <summary>
/// D3B0-A A.9 §19 -- one detector's attempt within a family.
/// <paramref name="RejectionReason"/> is OPAQUE and is never interpreted by this seam.
/// </summary>
public sealed record FamilyCandidateAttempt(
    DetectorId DetectorId,
    RiskLevel Level,
    ResolutionEvidence Evidence,
    RawSpan Span,
    CanonicalValue? Canonical,
    FamilyAttemptOutcome Outcome,
    string? RejectionReason);

/// <summary>D3B0-A A.9 §19 -- what a family arbiter emits and what it rejected.</summary>
public sealed record FamilyArbitrationResult(
    IReadOnlyList<DetectionCandidate> Emitted,
    IReadOnlyList<FamilyCandidateAttempt> Rejected);

/// <summary>
/// D3B0-A A.9 §19 -- the GENERIC taxpayer-family arbitration seam.
///
/// FAMILY_REJECTED_NEVER_EMITTED: an attempt with Outcome == Rejected MUST NOT appear in
/// <see cref="FamilyArbitrationResult.Emitted"/>, under any circumstance.
///
/// OUT OF SCOPE FOR D3B0, deliberately absent here: the ATIN &gt; ITIN &gt; SSN rule itself, any
/// arbiter implementation encoding taxpayer ordering, any registration of a taxpayer arbiter, and
/// every SSN/ITIN/ATIN grammar, range, exclusion, separator or boundary rule. Producing a
/// FamilyCandidateAttempt requires detector grammar, so NO arbiter is constructible or registrable
/// at the bridge baseline -- this file therefore contains an interface and records only, and
/// D3B0 ships ZERO implementations of it.
/// </summary>
public interface IDetectorFamilyArbiter
{
    string FamilyName { get; }
    FamilyArbitrationResult Arbitrate(IReadOnlyList<FamilyCandidateAttempt> attempts);
}
