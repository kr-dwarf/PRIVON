namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §16.2.
///
/// NOT_READY_IS_NOT_A_SHELL: <see cref="Detector"/> is ALWAYS a real implementation -- never
/// null, never a no-op/fake/identity-only/placeholder shell. A MISSING detector (no registration)
/// and a NOT-READY one (a registration whose Readiness differs from Ready) are distinct,
/// separately observable production states (§17.2).
/// </summary>
public sealed record DetectorRegistration(DetectorId Id, IDetector Detector, int ResolutionRank)
{
    /// <summary>
    /// QUERIED AT USE, never snapshotted at registration, because UnavailableAuthoritySnapshot
    /// depends on snapshot state at scan time (§20.2). Never cached, never reflected.
    /// </summary>
    public DetectorReadinessState Readiness => Detector.Readiness;
}
