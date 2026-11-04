using Privon.Detection;
using Privon.Storage;

namespace Privon.UsProductIntegration;

// [PRIVON-AI-HANDOFF]
// ROLE: The shipped product's EXPLICIT OPT-IN gate for the United States pack (P1): the layer ABOVE the per-country ON/OFF choice that says whether the
//       user has ever explicitly turned U.S. detection on, and the owner that serves the shipped (Korean + Global) pipeline until they have.
// TRUTH: Before an activation exists the product runs exactly DetectionPipeline.CreateDefault() -- the same nine detectors the app always ran -- and no
//        U.S. detector is constructed at all. That is NOT an "authenticated OFF" and nothing here fabricates one: the country setting is never consulted
//        and never written. Only an INTACT, user-action activation record opens the gate; from then on the existing CountryChoice contract governs
//        unchanged (an intact Off stops the pack, every other observation keeps it active -- FAIL_TOWARD_PROTECTION is untouched).
//        An activation record that cannot be verified (corrupt, failed authentication, newer format, an unusable key) keeps the pack OFF and is reported as
//        "cannot be verified" -- never as a normal OFF choice and never as ON. An authority that is not the fully usable production artifact makes the
//        pack UNAVAILABLE: it is not offered and not served, and nothing is called ready.
// FROZEN: Resolve is the one place the gate is decided; the owner, the Settings view and the tray all read it from here. A failure to READ the activation
//         is "cannot be verified" (the shipped pipeline keeps serving Korean / Global), never an exception that stops those scans.
// DO_NOT: Map "never selected" or "cannot be verified" to CountrySettingObservation.AuthenticatedOff, write a country choice or an activation from here,
//         activate on a default, or serve a U.S. pipeline before an intact activation.
// NAVIGATE: UsGatedOptInOwner (serves the pipeline), PrivonLocalStore.LoadCountryActivation / SaveCountryActivation, PrivonApp CountryChoiceService (the
//           only writer, from a Settings user action), UsReadinessAdapter in the app (maps the snapshot's gate to the tray line).

/// <summary>Where the explicit U.S. opt-in stands. <see cref="NotGated"/> is the zero value: a pipeline that is not gated at all (the experimental host's, tests).</summary>
public enum UsOptInGate
{
    NotGated = 0,

    /// <summary>The production authority is not fully usable: U.S. detection is neither offered nor run.</summary>
    Unavailable,

    /// <summary>The user has never explicitly activated U.S. detection: the shipped pipeline runs and no U.S. detector exists.</summary>
    NotSelected,

    /// <summary>An activation record exists but cannot be verified (corrupt, unauthenticated, newer format, no usable key): U.S. detection stays off.</summary>
    Unverifiable,

    /// <summary>An intact activation by the user: the U.S. pipeline runs, following the per-country choice contract.</summary>
    OptedIn,
}

public static class UsOptInGateRules
{
    /// <summary>The gate for an activation record's state and the authority's fitness. An intact On is the only way to <see cref="UsOptInGate.OptedIn"/>.</summary>
    public static UsOptInGate Resolve(CountryChoiceState activation, bool authorityUsable)
    {
        if (!authorityUsable) return UsOptInGate.Unavailable;
        return activation switch
        {
            CountryChoiceState.On => UsOptInGate.OptedIn,
            CountryChoiceState.Missing => UsOptInGate.NotSelected,
            _ => UsOptInGate.Unverifiable,   // Corrupt / Unauthenticated / UnknownVersion -- and an Off, which an activation record never holds
        };
    }

    /// <summary>The authority is usable only when it is the production-scope artifact with BOTH authority-backed detectors ready.</summary>
    public static bool IsUsable(UsAuthoritySet authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return authority.Origin == UsAuthorityOrigin.ProductionScope && authority.PhoneReady && authority.AddressReady;
    }
}

/// <summary>What the store can say about the user's explicit activation of the U.S. pack right now.</summary>
public interface IUsActivationSource
{
    /// <summary><see cref="CountryChoiceState.On"/> only for an intact user-action record; <see cref="CountryChoiceState.Missing"/> when never activated; any other state when it cannot be verified. Throwing is read as "cannot be verified".</summary>
    CountryChoiceState Read();
}

public sealed class StoreBackedUsActivationSource : IUsActivationSource
{
    public const string CountryId = "UnitedStates";

    private readonly PrivonLocalStore _store;

    public StoreBackedUsActivationSource(PrivonLocalStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    public CountryChoiceState Read() => _store.LoadCountryActivation(CountryId).State;
}

/// <summary>
/// Serves the shipped product's detection for the U.S. opt-in (P1): the shipped default pipeline until the user has explicitly activated U.S. detection,
/// then the U.S. opt-in pipeline of <see cref="UsOptInOwner"/> (verified production authority, the existing per-country choice contract).
/// </summary>
public sealed class UsGatedOptInOwner : IDisposable
{
    private readonly UsOptInOwner? _owner;
    private readonly IUsActivationSource _activation;
    private readonly bool _authorityUsable;
    private readonly PrivonLocalStore? _store;
    private readonly object _gate = new();
    private DetectionPipeline? _shipped;
    private int _disposed;

    private UsGatedOptInOwner(UsOptInOwner? owner, IUsActivationSource activation, bool authorityUsable, PrivonLocalStore? store)
    {
        _owner = owner;
        _activation = activation;
        _authorityUsable = authorityUsable;
        _store = store;
    }

    /// <summary>The shipped configuration: the verified production authority and the real store's country choice and activation records.</summary>
    public static UsGatedOptInOwner CreateProduction(PrivonLocalStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Create(new StoreBackedCountrySettingSource(store), new StoreBackedUsActivationSource(store), UsAuthoritySet.Production, store);
    }

    /// <summary>Any country-setting source, activation source and authority (tests, fixtures). An unusable authority yields an owner that serves the shipped pipeline only.</summary>
    public static UsGatedOptInOwner Create(ICountrySettingSource country, IUsActivationSource activation, UsAuthoritySet authority)
        => Create(country, activation, authority, store: null);

    private static UsGatedOptInOwner Create(ICountrySettingSource country, IUsActivationSource activation, UsAuthoritySet authority, PrivonLocalStore? store)
    {
        ArgumentNullException.ThrowIfNull(country);
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(authority);
        bool usable = UsOptInGateRules.IsUsable(authority);
        return new UsGatedOptInOwner(usable ? UsOptInOwner.Create(country, authority) : null, activation, usable, store);
    }

    /// <summary>
    /// The ONE store this owner reads its country choice and its activation from (null for an owner built from bare sources). The shipped composition adopts
    /// this very instance for everything else it does with the store -- Settings included -- so the screen and the detection can never be looking at two
    /// different opens of the same files (two opens can disagree about whether the master key was readable).
    /// </summary>
    public PrivonLocalStore? Store => _store;

    /// <summary>True when the production authority is fully usable, so U.S. detection can be offered at all.</summary>
    public bool AuthorityUsable => _authorityUsable;

    /// <summary>The gate right now, read from the activation record at this moment (never cached).</summary>
    public UsOptInGate CurrentGate()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        CountryChoiceState state;
        try { state = _activation.Read(); }
        catch (Exception) { state = CountryChoiceState.Unauthenticated; }   // cannot tell: U.S. stays off, Korean / Global keep running
        return UsOptInGateRules.Resolve(state, _authorityUsable);
    }

    /// <summary>The pipeline for THIS scan: the shipped default unless the user has activated U.S. detection, then the opt-in pipeline for the current country choice.</summary>
    public DetectionPipeline CurrentPipeline()
    {
        if (CurrentGate() == UsOptInGate.OptedIn) return _owner!.CurrentPipeline();
        return ShippedPipeline();
    }

    /// <summary>The readiness of what is serving scans right now. Before an activation it states the gate and no detector (none exists).</summary>
    public UsOptInReadinessSnapshot CurrentReadiness()
    {
        var gate = CurrentGate();
        if (gate == UsOptInGate.OptedIn) return _owner!.CurrentReadiness() with { Gate = UsOptInGate.OptedIn };
        return new UsOptInReadinessSnapshot(
            UsEnabled: false,
            _authorityUsable ? UsOptInProvenance.Production : UsOptInProvenance.NotSelected,
            Array.Empty<UsOptInDetectorStatus>(),
            ArtifactVersion: null,
            Gate: gate);
    }

    private DetectionPipeline ShippedPipeline()
    {
        lock (_gate) return _shipped ??= DetectionPipeline.CreateDefault();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _owner?.Dispose();
    }
}
