namespace Privon.Detection.Family;

/// <summary>
/// Supplies the per-detector attempts a family arbiter consumes, for a family whose members' own
/// <see cref="IDetector.Detect"/> output must never be final before family arbitration
/// (FAMILY_ATTEMPTS_ARE_INPUTS, A.17 §19.3). The product defines the arbitration SEAM
/// (<see cref="IDetectorFamilyArbiter"/>, <see cref="DetectorFamilyArbitrationBoundary"/>); producing an attempt
/// requires detector grammar, so a source is always supplied by the caller that owns that grammar. Privon.Detection
/// ships NO implementation.
///
/// USED ONLY by the explicit opt-in <see cref="DetectionPipeline"/> constructor that accepts sources. The default
/// pipeline, the list constructor and the registry-only constructor never read a source.
/// </summary>
public interface IDetectorFamilyAttemptSource
{
    /// <summary>The family name handed to <see cref="DetectorFamilyArbitrationBoundary"/>.</summary>
    string FamilyName { get; }

    /// <summary>
    /// The registered detectors that belong to the family. They are not run directly by the opt-in pipeline: their
    /// results become candidates only through arbitration.
    /// </summary>
    IReadOnlyCollection<DetectorId> MemberDetectorIds { get; }

    /// <summary>All attempts for this text; an empty list means the scan completed with nothing to arbitrate.</summary>
    IReadOnlyList<FamilyCandidateAttempt> CollectAttempts(DetectionContext context);
}
