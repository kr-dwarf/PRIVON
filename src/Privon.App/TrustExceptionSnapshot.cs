using Privon.Detection;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP6 -- the complete, already-mapped result of one
/// <see cref="ITrustExceptionProvider.Load"/> call: every recognized persisted trusted/exception
/// entry, translated into Detection's own typed values. Mapping is fully materialized before
/// this type is constructed -- no lazy enumeration over Storage entries, no lingering reference
/// to the original <c>TrustedPublicInfoEntry</c>/<c>ExceptionEntry</c> collections.
///
/// TRUST_EXCEPTION_SNAPSHOT_DIAGNOSTICS: <see cref="ToString"/> is explicitly overridden to
/// print only element counts -- it never enumerates or stringifies
/// <see cref="TrustedPublic"/>/<see cref="Exceptions"/>, so it can never recursively expose a
/// <see cref="CanonicalValue"/> even if a future element type ever regressed its own diagnostic
/// hardening (defense in depth, not reliance on <see cref="TrustedPublicValue"/>/
/// <see cref="AmbiguousExceptionValue"/>'s own Phase 3B STEP5.1 hardening alone).
/// </summary>
internal sealed class TrustExceptionSnapshot
{
    public IReadOnlyList<TrustedPublicValue> TrustedPublic { get; }
    public IReadOnlyList<AmbiguousExceptionValue> Exceptions { get; }

    public TrustExceptionSnapshot(IReadOnlyList<TrustedPublicValue> trustedPublic, IReadOnlyList<AmbiguousExceptionValue> exceptions)
    {
        ArgumentNullException.ThrowIfNull(trustedPublic);
        ArgumentNullException.ThrowIfNull(exceptions);
        TrustedPublic = trustedPublic;
        Exceptions = exceptions;
    }

    public override string ToString() =>
        $"{nameof(TrustExceptionSnapshot)} {{ TrustedPublicCount = {TrustedPublic.Count}, ExceptionCount = {Exceptions.Count} }}";
}
