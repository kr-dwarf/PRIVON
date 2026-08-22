namespace Privon.Detection;

/// <summary>
/// A Level 1-only user exception ("this is not personal information"). Kept as a distinct
/// type from <see cref="TrustedPublicValue"/> because the two stores have different level
/// eligibility rules (Level 3 may never be registered in either) and must not be
/// interchangeable at the type level.
///
/// TRUST_VALUE_DIAGNOSTIC_SURFACE (Phase 3B STEP5.1): <see cref="ToString"/> is explicitly
/// overridden to project only <see cref="PiiType"/> -- it never references
/// <see cref="CanonicalValue"/> at all, not even implicitly, so this type's own safety does not
/// depend on <see cref="Privon.Detection.CanonicalValue"/>'s own hardened override (Phase 3B
/// STEP3.1) continuing to exist/stay safe in the future. Same self-contained discipline already
/// used for <see cref="DetectionCandidate"/> not delegating to <c>Canonical.ToString()</c>.
/// </summary>
public readonly record struct AmbiguousExceptionValue(PiiType PiiType, CanonicalValue CanonicalValue)
{
    public override string ToString() => $"{nameof(AmbiguousExceptionValue)} {{ {nameof(PiiType)} = {PiiType} }}";
}
