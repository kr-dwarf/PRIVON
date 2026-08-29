namespace Privon.Storage;

/// <summary>
/// PRIVON v0.2.1 Gate 3B -- a single user-registered exact-value exception: the user has
/// explicitly chosen that this ONE specific canonical PII value should not be ordinarily
/// protected, regardless of whether it is genuinely private PII. Deliberately a DISTINCT type
/// from <see cref="ExceptionEntry"/> (Level1-only, "this isn't really PII") and
/// <see cref="TrustedPublicInfoEntry"/> (Level2-only, "the user has declared this value public/
/// trusted") -- same shape, same rationale for keeping the type distinct as
/// <c>Privon.Detection.AmbiguousExceptionValue</c>/<c>TrustedPublicValue</c> already establish
/// for their own two concepts: different product meaning, must never be interchangeable at the
/// type level even though the mechanical effect (Bypass) ends up the same.
///
/// Phase 2R.2-style schema: <see cref="PiiTypeId"/> is an opaque, stable string discriminator
/// (e.g. "Phone", "Email"), same ownership split as <see cref="ExceptionEntry.PiiTypeId"/> --
/// Storage never references Privon.Detection's PiiType enum directly. <see cref="Value"/> holds
/// <c>CanonicalValue.Value</c> exactly as Detection produced it -- never raw display formatting,
/// never re-normalized here (Storage owns no normalization logic of its own).
///
/// STORAGE_DTO_DIAGNOSTIC_SURFACE: <see cref="ToString"/> is explicitly overridden to exclude
/// <see cref="Value"/> entirely, identical precedent to <see cref="ExceptionEntry"/>/
/// <see cref="TrustedPublicInfoEntry"/> -- this holds real persisted PII in canonical form and
/// must never leak through an accidental interpolation/Debug.WriteLine/logger call.
/// </summary>
// [PRIVON-AI-HANDOFF]
// ROLE: Storage DTO for an explicit user-managed exact-value exception.
// TRUTH: Value contains canonical PII; PrivonLocalStore owns encrypted persistence.
// FROZEN: This meaning remains distinct from Level1 ExceptionEntry and Level2 TrustedPublicInfoEntry.
// DO_NOT: Normalize, log, or expose Value through diagnostic representations.
// NAVIGATE: PrivonLocalStore owns persistence; UserExceptionProvider and UserExceptionService own mapping and use.
public sealed record UserExceptionEntry(string PiiTypeId, string Value)
{
    public override string ToString() => $"{nameof(UserExceptionEntry)} {{ {nameof(PiiTypeId)} = {PiiTypeId} }}";
}
