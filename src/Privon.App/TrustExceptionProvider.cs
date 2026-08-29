using Privon.Detection;
using Privon.Storage;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP6 -- the only production implementation of <see cref="ITrustExceptionProvider"/>.
/// Wraps an already-open <see cref="PrivonLocalStore"/>, injected by the caller -- this type
/// never calls <see cref="PrivonLocalStore.OpenOrCreate"/> itself. APP_COMPOSITION_ROOT_STORAGE_PATH
/// (where the root directory for that store comes from) is frozen by
/// <see cref="PrivonAppComposition.ProductionStorageRootPath"/>; this type remains deliberately
/// agnostic to it -- it only ever receives an already-open <see cref="PrivonLocalStore"/> from its
/// caller.
///
/// CACHE_POLICY (Phase 3B STEP5 audit, confirmed here): <see cref="Load"/> re-reads
/// <see cref="PrivonLocalStore.LoadTrustedPublicInfo"/>/<see cref="PrivonLocalStore.LoadExceptions"/>
/// fresh on every call -- no App-side cache of either the raw Storage entries or the mapped
/// Detection values. This bounds the lifetime of the canonical PII this type ever touches to a
/// single <see cref="Load"/> call, avoids stale trust/exception data, and needs no settings-
/// change invalidation machinery (which does not exist yet). The only persistent field on this
/// type is the injected <see cref="PrivonLocalStore"/> itself -- no trusted/exception list, no
/// canonical value, no raw Storage entry is ever retained between calls.
///
/// STORAGE_FAILURE_POLICY: <see cref="PrivonLocalStore"/>'s own Load methods are already total --
/// every failure mode (missing file, DPAPI/decrypt/authentication failure, corrupt envelope,
/// corrupt JSON, structurally malformed entries) already collapses to a safe empty list before it
/// ever reaches this type. This provider does not re-implement or duplicate that fallback -- it
/// simply maps whatever valid list Storage hands back, empty or not. An empty list here can never
/// grant trust or create an exception (see <see cref="Privon.Detection.ExceptionTrustedEvaluator"/>'s
/// own exact-match-only semantics), so loss of persisted trust/exception data can only ever make
/// PRIVON more protective, never less.
/// </summary>
internal sealed class TrustExceptionProvider : ITrustExceptionProvider
{
    private readonly PrivonLocalStore _store;

    public TrustExceptionProvider(PrivonLocalStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public TrustExceptionSnapshot Load()
    {
        var trustedEntries = _store.LoadTrustedPublicInfo();
        var exceptionEntries = _store.LoadExceptions();

        var trustedPublic = new List<TrustedPublicValue>(trustedEntries.Count);
        foreach (var entry in trustedEntries)
        {
            if (TryMapCanonical(entry.PiiTypeId, entry.Value, out var canonical))
                trustedPublic.Add(new TrustedPublicValue(canonical.PiiType, canonical));
        }

        var exceptions = new List<AmbiguousExceptionValue>(exceptionEntries.Count);
        foreach (var entry in exceptionEntries)
        {
            if (TryMapCanonical(entry.PiiTypeId, entry.Value, out var canonical))
                exceptions.Add(new AmbiguousExceptionValue(canonical.PiiType, canonical));
        }

        return new TrustExceptionSnapshot(trustedPublic, exceptions);
    }

    /// <summary>
    /// PIITYPE_MAPPING: the ONLY translation this provider ever performs is
    /// <see cref="PiiTypeIdCodec.TryFromStableId"/> -- no <c>Enum.Parse</c>/<c>Enum.TryParse</c>/
    /// reflection/numeric cast/duplicate switch table. A stable ID the codec does not recognize
    /// (a future PiiType, a typo, drift) makes <see cref="TryMapCanonical"/> return false for
    /// that one entry only -- the caller skips it and continues with the rest; it never becomes
    /// trusted or an exception, and never aborts the whole snapshot.
    ///
    /// CANONICAL_VALUE_MAPPING: for a recognized entry, <paramref name="value"/> (Storage's
    /// already-frozen CANONICAL_VALUE_PERSISTENCE_CONTRACT payload) is used to construct
    /// <see cref="CanonicalValue"/> exactly as persisted -- no trim/lowercase/re-normalization/
    /// re-detection.
    ///
    /// TYPED_VALUE_PIITYPE_INVARIANT: the returned <see cref="CanonicalValue.PiiType"/> IS the
    /// same <paramref name="piiType"/> just resolved -- callers construct
    /// <see cref="TrustedPublicValue"/>/<see cref="AmbiguousExceptionValue"/> by reading
    /// <c>canonical.PiiType</c> back out of this same value (never a separately-tracked
    /// variable), so the outer wrapper's PiiType and its CanonicalValue's PiiType can never
    /// disagree by construction.
    /// </summary>
    private static bool TryMapCanonical(string piiTypeId, string value, out CanonicalValue canonical)
    {
        if (!PiiTypeIdCodec.TryFromStableId(piiTypeId, out var piiType))
        {
            canonical = default;
            return false;
        }

        canonical = new CanonicalValue(piiType, value);
        return true;
    }
}
