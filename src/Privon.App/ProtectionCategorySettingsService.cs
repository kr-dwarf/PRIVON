using Privon.Storage;

namespace Privon.App;

/// <summary>
/// PRIVON v0.2.1 Gate 3C -- the minimum non-UI mutation API a Settings UI needs to edit
/// <see cref="ProtectionCategorySettings"/>: Load/SetPhoneEnabled/SetEmailEnabled/Reset. Same
/// single-responsibility split already established by <see cref="UserExceptionService"/> (mutation
/// owner) vs. <see cref="ProtectionCategorySettingsProvider"/> (read path consumed by the
/// clipboard-privacy pipeline) -- deliberately a SEPARATE type from that provider. Wraps an
/// already-open <see cref="PrivonLocalStore"/> directly, injected by the caller.
///
/// NO_CACHE: every method here is a fresh load-modify-save against <see cref="PrivonLocalStore"/>
/// -- this type retains no <see cref="PrivonSettings"/>/<see cref="ProtectionCategorySettings"/>
/// between calls, matching <see cref="ProtectionCategorySettingsProvider"/>'s own CACHE_POLICY and
/// <see cref="UserExceptionService"/>'s own NO_IN_MEMORY_MIRROR discipline.
///
/// PRESERVE_UNRELATED_STATE (frozen contract for this Gate): every mutation reloads the
/// authoritative <see cref="PrivonSettings"/> first, changes ONLY the one requested
/// <see cref="ProtectionCategorySettings"/> field, and saves the WHOLE settings record back --
/// <see cref="PrivonSettings.ProtectionEnabled"/>/<see cref="PrivonSettings.PausedUntilUtc"/> and
/// every OTHER category field are carried through completely untouched via C#'s own `with`
/// non-destructive mutation, never reconstructed field-by-field by hand (which would risk silently
/// dropping a future <see cref="PrivonSettings"/>/<see cref="ProtectionCategorySettings"/> member
/// this Gate does not yet know about).
///
/// WRITE_FAILURE_POLICY: every method here calls straight through to
/// <see cref="PrivonLocalStore.SaveSettings"/>, which itself never catches or suppresses a genuine
/// write failure -- a failed Set*/Reset always propagates as a thrown exception (e.g.
/// MASTER_KEY_UNAVAILABLE -- see that method's own doc), never silently reports success. This
/// mirrors <see cref="UserExceptionService"/>'s own identical WRITE_FAILURE_POLICY exactly.
///
/// CONCURRENCY: identical reasoning to <see cref="UserExceptionService"/>'s own CONCURRENCY doc --
/// as of this Gate, every caller (a real WPF Settings window, singleton by construction --
/// see <see cref="SettingsCoordinator"/>) invokes this type's mutation methods only on the single
/// WPF Dispatcher thread, synchronously, one at a time -- no lock is added here pre-emptively.
/// </summary>
internal sealed class ProtectionCategorySettingsService
{
    private readonly PrivonLocalStore _store;

    public ProtectionCategorySettingsService(PrivonLocalStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Freshly loaded, every call -- see <see cref="ProtectionCategorySettingsProvider.Load"/>'s
    /// own identical CACHE_POLICY. Never a partially-populated value; resolves to
    /// <see cref="ProtectionCategorySettings.AllOn"/> whenever Storage itself would have.</summary>
    public ProtectionCategorySettings Load() => _store.LoadSettings().Categories ?? ProtectionCategorySettings.AllOn;

    public void SetPhoneEnabled(bool enabled) => Mutate(categories => categories with { PhoneEnabled = enabled });

    public void SetEmailEnabled(bool enabled) => Mutate(categories => categories with { EmailEnabled = enabled });

    /// <summary>Restores every category to ON -- the product-contract default -- while preserving
    /// every other <see cref="PrivonSettings"/> field exactly like every other mutation here.</summary>
    public void Reset() => Mutate(_ => ProtectionCategorySettings.AllOn);

    // PRESERVE_UNRELATED_STATE -- see class doc.
    private void Mutate(Func<ProtectionCategorySettings, ProtectionCategorySettings> transform)
    {
        var settings = _store.LoadSettings();
        var current = settings.Categories ?? ProtectionCategorySettings.AllOn;
        var updated = transform(current);
        _store.SaveSettings(settings with { Categories = updated });
    }
}
