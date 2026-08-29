using Privon.Storage;

namespace Privon.App;

/// <summary>
/// PRIVON v0.2.1 Gate 3A -- the only production implementation of
/// <see cref="IProtectionCategorySettingsProvider"/>. Wraps an already-open
/// <see cref="PrivonLocalStore"/>, injected by the caller -- this type never calls
/// <see cref="PrivonLocalStore.OpenOrCreate"/> itself, exactly like <see cref="TrustExceptionProvider"/>.
///
/// CACHE_POLICY: <see cref="Load"/> re-reads <see cref="PrivonLocalStore.LoadSettings"/> fresh on
/// every call -- no App-side cache. The only persistent field on this type is the injected
/// <see cref="PrivonLocalStore"/> itself; no <see cref="ProtectionCategorySettings"/> or
/// <see cref="PrivonSettings"/> value is ever retained between calls.
///
/// STORAGE_FAILURE_POLICY: <see cref="PrivonLocalStore.LoadSettings"/> is already total -- every
/// failure mode (missing file, decrypt/authentication failure, corrupt envelope, corrupt JSON,
/// pre-migration data missing the Categories property) already resolves to
/// <see cref="ProtectionCategorySettings.AllOn"/> before it ever reaches this type. The
/// <c>?? AllOn</c> below is deliberate defense-in-depth, not reliance on it being the only
/// guarantee: this type's own contract must hold even if that upstream resolution were ever
/// weakened by a future, unrelated change.
/// </summary>
internal sealed class ProtectionCategorySettingsProvider : IProtectionCategorySettingsProvider
{
    private readonly PrivonLocalStore _store;

    public ProtectionCategorySettingsProvider(PrivonLocalStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public ProtectionCategorySettings Load() => _store.LoadSettings().Categories ?? ProtectionCategorySettings.AllOn;
}
