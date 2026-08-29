using Privon.Detection;
using Privon.Storage;

namespace Privon.App;

/// <summary>
/// PRIVON v0.2.1 Gate 3B -- the only production implementation of
/// <see cref="IUserExceptionProvider"/>. Wraps an already-open <see cref="PrivonLocalStore"/>,
/// injected by the caller -- never calls <see cref="PrivonLocalStore.OpenOrCreate"/> itself, same
/// discipline as <see cref="TrustExceptionProvider"/>/<see cref="ProtectionCategorySettingsProvider"/>.
///
/// CACHE_POLICY: <see cref="Load"/> re-reads <see cref="PrivonLocalStore.LoadUserExceptions"/>
/// fresh on every call -- no App-side cache. The only persistent field is the injected
/// <see cref="PrivonLocalStore"/> itself.
///
/// PIITYPE_MAPPING: the ONLY translation this provider performs is
/// <see cref="PiiTypeIdCodec.TryFromStableId"/> -- identical discipline to
/// <see cref="TrustExceptionProvider.TryMapCanonical"/>. A stable ID the codec does not recognize
/// (a future PiiType, a typo, drift) makes that ONE entry skipped -- never a fabricated fallback
/// PiiType, never an aborted load for the rest of the list.
/// </summary>
internal sealed class UserExceptionProvider : IUserExceptionProvider
{
    private readonly PrivonLocalStore _store;

    public UserExceptionProvider(PrivonLocalStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public IReadOnlyList<UserExceptionValue> Load()
    {
        var entries = _store.LoadUserExceptions();

        var values = new List<UserExceptionValue>(entries.Count);
        foreach (var entry in entries)
        {
            if (PiiTypeIdCodec.TryFromStableId(entry.PiiTypeId, out var piiType))
                values.Add(new UserExceptionValue(piiType, new CanonicalValue(piiType, entry.Value)));
        }

        return values;
    }
}
