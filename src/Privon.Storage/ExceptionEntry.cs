namespace Privon.Storage;

/// <summary>
/// A Level 1-only exception entry (audit contract section 4/8).
///
/// Phase 2R.2 -- schema v2: <see cref="PiiTypeId"/> is an opaque, stable string discriminator
/// (e.g. "Phone", "Email") for the PII type this exception applies to. Storage deliberately
/// does not reference Privon.Detection's PiiType enum here -- Storage and Detection are
/// sibling projects (both depend only on Privon.Core) and this schema must not create a new
/// cross-dependency. The string<->PiiType mapping is owned by whichever caller composes
/// Storage and Detection together (see the Phase 2R report's
/// PERSISTED_TYPE_DISCRIMINATOR_STRING_OWNERSHIP note); Storage only ever treats this as an
/// opaque, non-empty string.
///
/// Both fields are validated as structurally non-empty by <see cref="PrivonLocalStore"/> on
/// load and on save -- never as a PII-format check (Storage is not a Phone/Email format
/// validator), only as a baseline "this entry isn't obviously malformed" guard.
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
/// <see cref="PrivonLocalStore"/>) reflects over the record's properties directly and does not
/// call ToString(), so the persisted JSON/encrypted-envelope shape is completely unaffected.
/// </summary>
public sealed record ExceptionEntry(string PiiTypeId, string Value)
{
    public override string ToString() => $"{nameof(ExceptionEntry)} {{ {nameof(PiiTypeId)} = {PiiTypeId} }}";
}
