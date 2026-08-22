namespace Privon.Detection;

/// <summary>
/// A normalized, comparable representation of a detected value's content (e.g. a phone
/// number with all separators stripped). Used only for two purposes: exact-match comparison
/// against Trusted/Exception entries, and grouping equal values under the same alias.
///
/// This is an in-memory-only, per-call artifact -- it is never written to disk, logs, or
/// telemetry, and its lifetime is scoped to a single detection/protection call, matching
/// the AliasMap "processing session only" contract from the design doc.
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
