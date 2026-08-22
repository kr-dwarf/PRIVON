namespace Privon.Storage;

/// <summary>
/// A Level 2, exact-match value the user has explicitly declared public/trusted (audit
/// contract section 4).
///
/// Phase 2R.2 -- schema v2: <see cref="PiiTypeId"/> is an opaque, stable string discriminator
/// (e.g. "Phone", "Email") for the PII type this trusted value applies to. Same rationale and
/// ownership split as <see cref="ExceptionEntry.PiiTypeId"/> -- see that type's doc comment.
///
/// STORAGE_DTO_DIAGNOSTIC_SURFACE (Phase 3B STEP5.1): <see cref="ToString"/> is explicitly
/// overridden to exclude <see cref="Value"/> entirely -- per the frozen
/// CANONICAL_VALUE_PERSISTENCE_CONTRACT (Phase 2R.5), <see cref="Value"/> holds
/// <c>CanonicalValue.Value</c>, i.e. real persisted PII in canonical form. The
/// record-synthesized ToString() this type would otherwise get prints every property including
/// <see cref="Value"/>, which would turn any accidental interpolation/Debug.WriteLine/logger
/// call into a leak of decrypted persisted PII. Only <see cref="PiiTypeId"/> is safe to print --
/// no length, hash, prefix, or other content-derived value is included either, matching the
/// identical precedent already established for Privon.Detection.CanonicalValue (Phase 3B
/// STEP3.1) and Privon.Windows.ClipboardTextSnapshot (Phase 3A.4 STEP3.2). This override affects
/// diagnostic representation only -- System.Text.Json serialization (used by
/// <see cref="Privon.Storage.PrivonLocalStore"/>) reflects over the record's properties
/// directly and does not call ToString(), so the persisted JSON/encrypted-envelope shape is
/// completely unaffected.
/// </summary>
public sealed record TrustedPublicInfoEntry(string PiiTypeId, string Value)
{
    public override string ToString() => $"{nameof(TrustedPublicInfoEntry)} {{ {nameof(PiiTypeId)} = {PiiTypeId} }}";
}
