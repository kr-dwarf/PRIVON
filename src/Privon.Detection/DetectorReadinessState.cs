namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §16.2 -- closed at exactly two members. <see cref="Ready"/> is the frozen D3A.1
/// default ExpectedDetectorState; <see cref="UnavailableAuthoritySnapshot"/> is the only
/// not-ready state accepted authority names. A future not-ready reason requires an audited
/// amendment. Not a CCSK field; validated at §16.3, never at R0.5.
/// </summary>
public enum DetectorReadinessState
{
    Ready = 0,
    UnavailableAuthoritySnapshot = 1,
}
