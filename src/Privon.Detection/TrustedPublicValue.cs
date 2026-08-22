namespace Privon.Detection;

/// <summary>
/// A Level 2, exact-match value the user has explicitly declared public/trusted. This is a
/// Detection-owned type (not a reference to Privon.Storage's persistence record) so
/// Detection has no dependency on Storage; the caller is responsible for loading entries
/// from storage and mapping them to this type before invoking Detection.
///
/// TRUST_VALUE_DIAGNOSTIC_SURFACE (Phase 3B STEP5.1): <see cref="ToString"/> is explicitly
/// overridden to project only <see cref="PiiType"/> -- it never references
/// <see cref="CanonicalValue"/> at all, not even implicitly, so this type's own safety does not
/// depend on <see cref="Privon.Detection.CanonicalValue"/>'s own hardened override (Phase 3B
/// STEP3.1) continuing to exist/stay safe in the future. Same self-contained discipline already
/// used for <see cref="DetectionCandidate"/> not delegating to <c>Canonical.ToString()</c>.
/// </summary>
public readonly record struct TrustedPublicValue(PiiType PiiType, CanonicalValue CanonicalValue)
{
    public override string ToString() => $"{nameof(TrustedPublicValue)} {{ {nameof(PiiType)} = {PiiType} }}";
}
