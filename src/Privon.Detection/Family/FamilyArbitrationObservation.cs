namespace Privon.Detection.Family;

/// <summary>
/// D3B0-A A.17 §19.2 -- closed at exactly two members. A future not-available reason requires an
/// audited amendment. This state is a real boundary observation only: the absence of production
/// code is never represented by it (PRODUCTION_BOUNDARY_ABSENCE_IS_NOT_A_PRODUCTION_OBSERVATION).
/// </summary>
public enum FamilyArbitrationState
{
    ArbitrationAvailable,
    ArbitrationUnavailable,
}

/// <summary>
/// D3B0-A A.17 §19.1 -- what <see cref="DetectorFamilyArbitrationBoundary.TryArbitrate"/> observed.
/// <paramref name="AttemptCount"/> is the number of attempts supplied, so an auditor can confirm
/// lawful inputs reached the real boundary.
///
/// FAMILY_AVAILABILITY_IS_NOT_A_UNIT_AND_NOT_A_VERDICT (§19.3): not a RULE-U unit, not a RED/GREEN
/// verdict, not a D3A disposition.
/// </summary>
public sealed record FamilyArbitrationObservation(
    string FamilyName, FamilyArbitrationState State, int AttemptCount);
