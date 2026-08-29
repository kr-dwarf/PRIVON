namespace Privon.Detection;

/// <summary>
/// A normalized, comparable representation of a detected value's content (e.g. a phone
/// number with all separators stripped). Used only for two purposes: exact-match comparison
/// against Trusted/Exception entries, and grouping equal values under the same alias.
///
/// PERSISTENCE (corrected -- PRIVON v0.2.1 privacy contract consistency correction; this
/// paragraph previously read "never written to disk, logs, or telemetry," which was accurate
/// for Detection's own behavior but overbroad as a claim about CanonicalValue.Value system-wide
/// once Gate 3A/3B shipped): Privon.Detection itself never persists a CanonicalValue instance,
/// logs it, or sends it over a network -- no reference to Privon.Storage or any I/O exists
/// anywhere in this project. Within the clipboard-protection pipeline, a CanonicalValue's own
/// lifetime is scoped to a single detection/protection call, matching the AliasMap "processing
/// session only" contract from the design doc.
///
/// This does NOT mean CanonicalValue.Value is never written to disk anywhere in PRIVON:
/// PRIVON v0.2.1 Gate 3A/3B deliberately copies this exact string into
/// <c>Privon.Storage.ExceptionEntry</c>/<c>TrustedPublicInfoEntry</c>/<c>UserExceptionEntry</c>
/// and persists it locally, encrypted at rest under the existing master-key/DPAPI
/// infrastructure -- see those three types' own CANONICAL_VALUE_PERSISTENCE_CONTRACT docs.
/// <c>UserExceptionEntry</c> (<c>Privon.App.UserExceptionService</c>) in particular persists it
/// specifically so the user can manage exact per-value exceptions -- intentional,
/// purpose-bound, local, encrypted persistence, never logged/telemetried/networked, and never a
/// behavior of Privon.Detection or this type. A <see cref="CanonicalValue"/> re-hydrated from
/// that persisted Storage (e.g. via <c>Privon.App.UserExceptionProvider</c>, for Settings UI
/// display) is likewise not bound to "a single detection/protection call" -- its lifetime is
/// whatever its own caller (e.g. an open Settings window) retains it for.
///
/// DETECTION_CANONICAL_DIAGNOSTIC_SURFACE (Phase 3B STEP3.1): <see cref="ToString"/> is
/// explicitly overridden to exclude <see cref="Value"/> entirely -- the record-synthesized
/// ToString() this type would otherwise get prints every property including Value, which would
/// turn any accidental interpolation/Debug.WriteLine/logger call/exception message into a
/// sensitive normalized-PII leak (e.g. a bare phone number). Only PiiType is safe to print;
/// no length, hash, prefix, or other content-derived value is included either -- the safest
/// contract is no content-derived metadata at all, matching the identical precedent already
/// established for Privon.Windows.ClipboardTextSnapshot (Phase 3A.4 STEP3.2).
/// </summary>
public readonly record struct CanonicalValue(PiiType PiiType, string Value)
{
    public override string ToString() => $"{nameof(CanonicalValue)} {{ {nameof(PiiType)} = {PiiType} }}";
}
