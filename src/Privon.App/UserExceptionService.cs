using Privon.Detection;
using Privon.Storage;

namespace Privon.App;

/// <summary>
/// PRIVON v0.2.1 Gate 3B -- the minimum mutation API the Settings UI needs to edit the
/// UserExceptionDictionary: List/Add/Delete/Reset (PRIVON v0.2.1 Gate 3C: <see cref="SettingsCoordinator"/>
/// is now this type's own production caller -- see that type's own doc). Deliberately a SEPARATE type from
/// <see cref="UserExceptionProvider"/> (read path consumed by the clipboard-privacy pipeline) --
/// same single-responsibility split as the rest of this Gate's design. Wraps an already-open
/// <see cref="PrivonLocalStore"/> directly, injected by the caller.
///
/// NO_IN_MEMORY_MIRROR: no method here retains a list/cache between calls -- <see cref="List"/> is
/// load-only, <see cref="Add"/>/<see cref="Delete"/> are each a fresh load-modify-save against
/// <see cref="PrivonLocalStore"/>, and <see cref="Reset"/> saves an empty list directly without a
/// prior load -- so it is always the persisted store itself that is the single source of truth the
/// Settings UI reads and writes through, never a second, independently-drifting copy.
///
/// CONCURRENCY (PRIVON v0.2.1 Gate 3B audit, documented rather than pre-emptively defended
/// against; corrected PRIVON v0.2.1 Gate 3C -- Settings UI now exists and is this type's own
/// production caller): nothing in this assembly's production code calls
/// <c>PrivonLocalStore.SaveSettings</c>/<c>SaveTrustedPublicInfo</c>/<c>SaveExceptions</c>, or
/// this type's own Add/Delete/Reset, from more than one thread -- <see cref="SettingsCoordinator"/>
/// is the only production caller, and it is reached exclusively from the single WPF dispatcher
/// thread (see that type's own CONCURRENCY doc), and <see cref="ClipboardPrivacyCoordinator"/>'s
/// own single dedicated worker thread never calls any Save* method at all (it only ever Loads). A
/// naive load-modify-save therefore cannot lose a concurrent update today, because no concurrent
/// caller is mechanically reachable. This is NOT a general guarantee for the type itself -- if a
/// future caller ever invokes Add/Delete/Reset from more than one thread concurrently, a lost
/// update becomes possible and must be re-audited at that time; no lock is added here
/// pre-emptively.
///
/// WRITE_FAILURE_POLICY: every method here calls straight through to
/// <see cref="PrivonLocalStore.SaveUserExceptions"/>, which itself never catches or suppresses a
/// genuine write failure (<see cref="AtomicFileWriter.WriteAtomic"/>'s own bare <c>catch { ...;
/// throw; }</c> always rethrows) -- a failed Add/Delete/Reset always propagates as a thrown
/// exception, never silently reports success.
/// </summary>
// [PRIVON-AI-HANDOFF]
// ROLE: App mutation owner for listing, adding, deleting, and resetting user exceptions.
// TRUTH: It retains no cache; Add/Delete update fresh persisted state and Reset writes an empty collection.
// FROZEN: Add accepts only Phone/Email and checks PiiType tag agreement with CanonicalValue.PiiType.
// DO_NOT: Treat that tag check as content validation; this service neither detects nor canonicalizes raw input.
// NAVIGATE: SettingsCoordinator owns raw-input detection; UserExceptionProvider owns persisted read mapping.
internal sealed class UserExceptionService
{
    private readonly PrivonLocalStore _store;

    public UserExceptionService(PrivonLocalStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Reuses the exact same PiiTypeIdCodec-based mapping (and the exact same
    /// unknown-entry-skipped, never-fabricated-fallback-PiiType discipline) as
    /// <see cref="UserExceptionProvider.Load"/> -- deliberately delegates to it rather than
    /// duplicating the mapping loop, so there is exactly one place this translation is written.</summary>
    public IReadOnlyList<UserExceptionValue> List() => new UserExceptionProvider(_store).Load();

    /// <summary>PRIVON v0.2.1 Gate 3C -- the only PiiType values <see cref="Add"/> will ever
    /// persist through this public mutation service. Deliberately just the two ordinary types the
    /// approved 0.2.1 Settings UI exposes -- NOT a change to Level3/policy semantics (see
    /// <see cref="Add"/>'s own ADD_ALLOWLIST doc for why).</summary>
    private static readonly IReadOnlyCollection<PiiType> AddableTypes = [PiiType.Phone, PiiType.Email];

    /// <summary>Idempotent: adding the same (PiiType, CanonicalValue) identity twice leaves
    /// exactly one persisted entry.
    ///
    /// ADD_ALLOWLIST (PRIVON v0.2.1 Gate 3C, defense-in-depth): rejects every <see cref="PiiType"/>
    /// other than <see cref="PiiType.Phone"/>/<see cref="PiiType.Email"/> BEFORE any Storage read or
    /// write -- including every Level3 type (<see cref="PiiType.ResidentRegistrationNumber"/>/
    /// <see cref="PiiType.CardNumber"/>/<see cref="PiiType.BankAccountNumber"/>/<see cref="PiiType.Secret"/>)
    /// and every currently-unsupported-product-scope type (<see cref="PiiType.IpAddress"/>/
    /// <see cref="PiiType.MacAddress"/>/<see cref="PiiType.GpsCoordinate"/>). This does NOT change
    /// Gate 3B's own policy semantics -- <c>UserExceptionPolicyEvaluator</c> was already
    /// structurally unable to let a Level3 candidate reach <c>Protect</c> in the first place (see
    /// its own doc), so a Level3 exception could never have been effective even if persisted. This
    /// guard exists purely to stop the public mutation service itself from persisting an
    /// unsupported/high-risk value unnecessarily -- e.g. a caller experimenting with a future
    /// PiiType before its own product/UI support exists.</summary>
    public void Add(PiiType piiType, CanonicalValue canonicalValue)
    {
        RequireConsistentPiiType(piiType, canonicalValue);
        RequireAddableType(piiType);
        var newEntry = new UserExceptionEntry(PiiTypeIdCodec.ToStableId(piiType), canonicalValue.Value);

        var current = _store.LoadUserExceptions();
        if (current.Contains(newEntry)) return; // already present -- no-op, no unnecessary write

        var updated = new List<UserExceptionEntry>(current) { newEntry };
        _store.SaveUserExceptions(updated);
    }

    /// <summary>Removes only the exact (PiiType, CanonicalValue) match. Deleting an absent entry
    /// is a safe, idempotent no-op (including on an empty/missing store) -- never throws, never
    /// touches any other entry.</summary>
    public void Delete(PiiType piiType, CanonicalValue canonicalValue)
    {
        RequireConsistentPiiType(piiType, canonicalValue);
        var target = new UserExceptionEntry(PiiTypeIdCodec.ToStableId(piiType), canonicalValue.Value);

        var current = _store.LoadUserExceptions();
        var updated = current.Where(e => e != target).ToList();
        if (updated.Count == current.Count) return; // target was never present -- no-op, no unnecessary write

        _store.SaveUserExceptions(updated);
    }

    /// <summary>Saves an empty list -- the entire dictionary, not merely the caller's own
    /// entries.</summary>
    public void Reset() => _store.SaveUserExceptions([]);

    // TYPED_VALUE_PIITYPE_INVARIANT (write-side): a caller passing a PiiType that disagrees with
    // canonicalValue.PiiType would silently persist an entry that could never match anything a
    // real DetectionCandidate ever produces (candidate.PiiType always equals candidate.Canonical.PiiType
    // by construction) -- fail loudly here instead of accepting a permanently-inert malformed entry.
    private static void RequireConsistentPiiType(PiiType piiType, CanonicalValue canonicalValue)
    {
        if (piiType != canonicalValue.PiiType)
        {
            throw new ArgumentException(
                $"{nameof(piiType)} ({piiType}) does not match {nameof(canonicalValue)}.PiiType ({canonicalValue.PiiType}).",
                nameof(canonicalValue));
        }
    }

    // ADD_ALLOWLIST enforcement -- see Add's own doc above.
    private static void RequireAddableType(PiiType piiType)
    {
        if (!AddableTypes.Contains(piiType))
        {
            throw new ArgumentException(
                $"UserExceptionService.Add only accepts {string.Join('/', AddableTypes)} (got {piiType}).",
                nameof(piiType));
        }
    }
}
