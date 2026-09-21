namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §6. <see cref="Name"/>, <see cref="PiiType"/> and <see cref="Detect"/> are
/// UNCHANGED by the bridge. The three new members are ABSTRACT and NEVER DEFAULTED -- for
/// <see cref="Readiness"/> in particular a default of Ready would be the FAIL-OPEN direction,
/// letting a snapshot-dependent detector run with missing authority data, so each implementer
/// states it literally.
///
/// DETECTOR_NAME_IS_NOT_AN_IDENTITY: <see cref="Name"/> is never read by the registry, any
/// comparator, any arbitration path, or any test that resolves a detector.
/// </summary>
public interface IDetector
{
    string Name { get; }
    PiiType PiiType { get; }
    IReadOnlyList<DetectionCandidate> Detect(DetectionContext context);

    /// <summary>§5.1 stable contract identity -- never derived from the class name.</summary>
    DetectorId Id { get; }

    /// <summary>§5.2 frozen D2.2 numeric rank -- a literal, never inferred from order.</summary>
    int ResolutionRank { get; }

    /// <summary>§16.2 readiness, queried at use and never cached.</summary>
    DetectorReadinessState Readiness { get; }
}
