using Privon.App;
using Privon.Detection.Scope;
using Privon.Storage;
using Privon.UsProductIntegration;

namespace Privon.UsOptInHost;

// [PRIVON-AI-HANDOFF]
// ROLE: Builds what the app is handed for the U.S. opt-in: the owner of the explicit opt-in (UsOptInOwner), over the SAME storage root the app uses, as
//       the pipeline delegate, the readiness delegate and the lifetime.
// TRUTH: The pipeline and the readiness both come from the one owner, so the number the tray shows is read from the registry of the pipeline that serves
//        scans. The country setting comes from the store country-choice record (StoreBackedCountrySettingSource): what the Settings control stored
//        is what the next pipeline follows, and anything that cannot be verified leaves the U.S. pack active. No setting is authenticated here.
// FROZEN: BuildProduction refuses (throws, builds nothing) unless the verified production authority is fully usable -- an experimental host that says
//         "production authority" never runs on less. Build(...) with another authority exists for tests and is labelled by that authority own origin.
// DO_NOT: Register detectors anywhere, cache a snapshot, or let a Provided / missing authority be shown as production.

public static class UsOptInSourcesBuilder
{
    /// <summary>The experimental host configuration: the explicitly selected, verified production authority. Throws if it is not fully usable.</summary>
    public static UsOptInSources BuildProduction(string storageRoot) => BuildVerified(storageRoot, UsAuthoritySet.Production);

    /// <summary>
    /// Builds only on an authority that is production-scope AND fully usable (phone and address both ready); anything else -- none, a shape-only fixture, a
    /// damaged or incomplete one -- throws and builds nothing. BuildProduction is this with the shipped production authority.
    /// </summary>
    public static UsOptInSources BuildVerified(string storageRoot, UsAuthoritySet authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (authority.Origin != UsAuthorityOrigin.ProductionScope || !authority.PhoneReady || !authority.AddressReady)
        {
            throw new InvalidOperationException("The production authority is not fully usable, so the experimental U.S. opt-in is not assembled.");
        }

        return Build(storageRoot, authority);
    }

    /// <summary>Any authority the caller chooses (none, a fixture, a damaged one): its readiness is reported as it is, labelled by its own origin.</summary>
    public static UsOptInSources Build(string storageRoot, UsAuthoritySet? authority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        var store = PrivonLocalStore.OpenOrCreate(storageRoot);
        var owner = UsOptInOwner.Create(new StoreBackedCountrySettingSource(store, CountryPack.UnitedStates), authority);
        return new UsOptInSources(
            owner.CurrentPipeline,
            () => UsReadinessAdapter.From(owner.CurrentReadiness()),
            owner);
    }
}
