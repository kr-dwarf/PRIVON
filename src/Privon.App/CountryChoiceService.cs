using Privon.Storage;
using Privon.UsProductIntegration;

namespace Privon.App;

// [PRIVON-AI-HANDOFF]
// ROLE: The Settings-side owner of the United States ON/OFF choice: stores a user's choice and reads back what the store can verify.
// TRUTH: SetUnitedStates records the choice with the SettingsUserAction origin and then RE-READS it; it returns only when the store reads back exactly
//        the requested choice. That the call came from a user input is the Settings surface's property; this service cannot see a person, and the origin
//        it writes is a claim by the caller (SettingsCoordinator, on a surface event), not a proof of intent.
// FROZEN: A request is applied only if it came through the one coordinator handler that exists only when this service was supplied (the explicit internal
//         opt-in). A repeated request for the state already stored writes nothing. A record of a newer format version is never overwritten.
//         SHIPPED OPT-IN (P1): when built with an activation store (ForShippedOptIn), turning United States detection ON records TWO things, in this order --
//         the country choice On, then the activation ("the user explicitly turned it on") -- each read back; turning it OFF records only the choice Off.
//         The control shows the EFFECTIVE state: unchecked until an intact activation exists, whatever the country record says. A request that fails
//         part-way leaves the activation absent, so detection stays off and the screen (re-read from the store) says so.
// DO_NOT: Call this from anywhere but SettingsCoordinator's United States handler, show a choice as stored before it reads back, or use this to enable
//         the United States detectors (the opt-in pipeline owner decides that; this only records the user's choice and activation).
// NAVIGATE: SettingsCoordinator.HandleCountryToggle; PrivonLocalStore.SaveCountryChoice / LoadCountryChoice; the tools-side StoreBackedCountrySettingSource.

/// <summary>The two store operations this service needs, as a seam so that "the save reported success but the choice did not read back" can be exercised.</summary>
internal interface ICountryChoiceStore
{
    CountryChoiceRead Load(string country);

    void Save(string country, bool enabled, CountryChoiceOrigin origin);
}

internal sealed class PrivonLocalCountryChoiceStore(PrivonLocalStore store) : ICountryChoiceStore
{
    public CountryChoiceRead Load(string country) => store.LoadCountryChoice(country);

    public void Save(string country, bool enabled, CountryChoiceOrigin origin) => store.SaveCountryChoice(country, enabled, origin);
}

/// <summary>The two store operations the shipped opt-in needs for the ACTIVATION record (a seam, like <see cref="ICountryChoiceStore"/>).</summary>
internal interface ICountryActivationStore
{
    CountryChoiceRead Load(string country);

    void Save(string country, CountryChoiceOrigin origin);
}

internal sealed class PrivonLocalCountryActivationStore(PrivonLocalStore store) : ICountryActivationStore
{
    public CountryChoiceRead Load(string country) => store.LoadCountryActivation(country);

    public void Save(string country, CountryChoiceOrigin origin) => store.SaveCountryActivation(country, origin);
}

internal sealed class CountryChoiceService
{
    public const string UnitedStatesId = "UnitedStates";

    private readonly ICountryChoiceStore _store;
    private readonly ICountryActivationStore? _activation;
    private readonly Func<bool>? _authorityUsable;

    public CountryChoiceService(PrivonLocalStore store)
        : this(new PrivonLocalCountryChoiceStore(store ?? throw new ArgumentNullException(nameof(store))))
    {
    }

    internal CountryChoiceService(ICountryChoiceStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    // The shipped opt-in (P1): the service also owns the activation record, and the control shows the effective state under the gate.
    internal CountryChoiceService(ICountryChoiceStore store, ICountryActivationStore activation, Func<bool> authorityUsable)
        : this(store)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(authorityUsable);
        _activation = activation;
        _authorityUsable = authorityUsable;
    }

    /// <summary>The shipped configuration: the real store for both records, and the owner's statement of whether the production authority is usable.</summary>
    internal static CountryChoiceService ForShippedOptIn(PrivonLocalStore store, Func<bool> authorityUsable)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new CountryChoiceService(new PrivonLocalCountryChoiceStore(store), new PrivonLocalCountryActivationStore(store), authorityUsable);
    }

    /// <summary>The stored United States choice as the store can verify it right now.</summary>
    public CountryChoiceRead LoadUnitedStates() => _store.Load(UnitedStatesId);

    /// <summary>
    /// Stores the user's choice and returns the verified read-back. Throws -- leaving the previous record as it was -- when it cannot be stored or
    /// the read-back is not the requested choice (no usable key, a newer-format record, an IO failure, or a record that does not verify).
    /// </summary>
    public CountryChoiceRead SetUnitedStates(bool enabled)
    {
        if (_activation is null) return SetChoice(enabled);

        if (!_authorityUsable!())
        {
            throw new InvalidOperationException("United States detection is not available: the production authority is not fully usable.");
        }

        var read = SetChoice(enabled);
        if (enabled && _activation.Load(UnitedStatesId).State != CountryChoiceState.On)
        {
            _activation.Save(UnitedStatesId, CountryChoiceOrigin.SettingsUserAction);
            if (_activation.Load(UnitedStatesId).State != CountryChoiceState.On)
            {
                throw new InvalidOperationException("The activation was not read back as the user's activation.");
            }
        }

        return read;
    }

    private CountryChoiceRead SetChoice(bool enabled)
    {
        var wanted = enabled ? CountryChoiceState.On : CountryChoiceState.Off;

        var current = _store.Load(UnitedStatesId);
        if (current.State == wanted) return current;                    // a repeated request: nothing to write

        _store.Save(UnitedStatesId, enabled, CountryChoiceOrigin.SettingsUserAction);

        var after = _store.Load(UnitedStatesId);
        if (after.State != wanted)
        {
            throw new InvalidOperationException("The country choice was not read back as the requested choice.");
        }

        return after;
    }

    /// <summary>
    /// What the Settings control shows: the EFFECTIVE state (only a verified Off is unchecked), why it is that, and whether a change could be stored.
    /// </summary>
    public UnitedStatesChoiceView ViewUnitedStates(bool masterKeyUnavailable)
    {
        var read = _store.Load(UnitedStatesId);
        bool choiceEditable = !masterKeyUnavailable && read.State != CountryChoiceState.UnknownVersion;
        if (_activation is null)
        {
            return new UnitedStatesChoiceView(Enabled: read.State != CountryChoiceState.Off, Basis: read.State, Editable: choiceEditable);
        }

        // The shipped opt-in: nothing is ON before an intact activation, however the country record reads.
        var activation = _activation.Load(UnitedStatesId);
        var gate = UsOptInGateRules.Resolve(activation.State, _authorityUsable!());
        return gate switch
        {
            UsOptInGate.OptedIn => new UnitedStatesChoiceView(read.State != CountryChoiceState.Off, read.State, choiceEditable, gate),
            UsOptInGate.Unavailable => new UnitedStatesChoiceView(false, read.State, false, gate),
            UsOptInGate.Unverifiable => new UnitedStatesChoiceView(false, read.State, choiceEditable && activation.State != CountryChoiceState.UnknownVersion, gate),
            _ => new UnitedStatesChoiceView(false, read.State, choiceEditable, gate),
        };
    }
}
