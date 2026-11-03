using Privon.Detection;
using Privon.Detection.Scope;
using Privon.Storage;

namespace Privon.UsProductIntegration;

// ISOLATED PRODUCT-INTEGRATION CANDIDATE -- NOT REGISTERED, NOT ACTIVATED. See UsCandidateFactory.cs.
//
// T-1 (trusted observation) and T-2 (following the setting) for the opt-in U.S. pipeline.
//
// WHAT THE REAL SETTINGS STORE GUARANTEES (checked against the source, not assumed):
//   * PrivonLocalStore encrypts every file with AES-GCM under a master key that Windows DPAPI (CurrentUser) protects, with the store purpose bound
//     as AAD: another Windows account, or an offline edit of the file, cannot forge or alter it. A process running as the SAME user can still do
//     both, so this is integrity of storage and per-user scope, not proof of a human act.
//   * PrivonSettings (settings.bin) holds ProtectionEnabled, PausedUntilUtc and five category booleans. It has NO country field and is not changed.
//   * Since 0.4.0 the store has its OWN record for the per-country choice (country-settings.bin, its own purpose and format version, a revision
//     that only goes up, an origin marker). Reading it gives AuthenticatedOn / AuthenticatedOff only for an INTACT record of a known version and
//     origin; anything else is a non-choice (Missing / Corrupt / Unauthenticated / UnknownVersion), which the detection scope treats as "stays active".
//   * The CHOICE is written by exactly one product path: the Settings United States control (SettingsCoordinator -> CountryChoiceService ->
//     PrivonLocalStore.SaveCountryChoice, origin SettingsUserAction), offered only by an explicit opt-in composition. A shipped launch that was never turned
//     on therefore reads Missing, and an AuthenticatedOff exists only after that control stored one. The record's origin marker is a claim by the writing
//     code; it is not evidence of a person. (The shipped opt-in additionally requires an activation record, which this source never reads: UsOptInGate.)

public interface ICountrySettingSource
{
    /// <summary>
    /// The current observation. Throwing means "cannot tell": the scan fails; it is never read as OFF and never answered
    /// with a previously built pipeline.
    /// </summary>
    CountrySettingObservation Read();
}

/// <summary>
/// The only source backed by the real store. It maps what the store can verify about this pack's country choice: an intact
/// user-action record is <see cref="CountrySettingObservation.AuthenticatedOn"/> / <see cref="CountrySettingObservation.AuthenticatedOff"/>;
/// no record, a damaged or unauthenticated one, or one of a newer version is the matching non-choice (<see cref="CountrySettingObservation.Missing"/>,
/// <see cref="CountrySettingObservation.Corrupt"/>, <see cref="CountrySettingObservation.Unauthenticated"/>,
/// <see cref="CountrySettingObservation.UnknownVersion"/>), which never stops the pack. It takes no text.
/// </summary>
public sealed class StoreBackedCountrySettingSource : ICountrySettingSource
{
    private readonly PrivonLocalStore _store;
    private readonly string _countryId;

    /// <summary>Reads the persisted choice of ONE country pack (the pack is an enum, never text). Other packs' choices are independent.</summary>
    public StoreBackedCountrySettingSource(PrivonLocalStore store, CountryPack country = CountryPack.UnitedStates)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (!Enum.IsDefined(country)) throw new ArgumentOutOfRangeException(nameof(country));
        _countryId = country.ToString();
    }

    /// <summary>
    /// The observation for this pack. Only an intact, known-version, known-origin record is an Authenticated ON / OFF; a missing key, a failed
    /// authentication, a damaged or older record, an unknown version or an unknown origin is the matching non-choice -- never an OFF.
    /// </summary>
    public CountrySettingObservation Read() => _store.LoadCountryChoice(_countryId).State switch
    {
        CountryChoiceState.On => CountrySettingObservation.AuthenticatedOn,
        CountryChoiceState.Off => CountrySettingObservation.AuthenticatedOff,
        CountryChoiceState.Missing => CountrySettingObservation.Missing,
        CountryChoiceState.Corrupt => CountrySettingObservation.Corrupt,
        CountryChoiceState.UnknownVersion => CountrySettingObservation.UnknownVersion,
        _ => CountrySettingObservation.Unauthenticated,   // Unauthenticated, and anything this build does not recognize: never an OFF
    };
}

/// <summary>
/// Builds the immutable opt-in pipeline for the CURRENT observation and hands it out per scan.
///
///   * The setting is read at the start of every <see cref="Current"/> call, so a scan that starts after a change has
///     completed can never use the pipeline built for the previous value.
///   * A pipeline is a pure function of the observation: it is rebuilt only when the observation differs from the one it was
///     built for, exactly once even under concurrent callers, and swapped atomically (under one lock).
///   * A scan that already holds a pipeline finishes with it (pipelines are immutable); it never sees a half-built one.
///   * If the setting cannot be read, the observation is undefined, or the new pipeline cannot be built, <see cref="Current"/>
///     throws. The previously built pipeline is dropped on a rebuild attempt and never served as a fallback, and a failure is
///     never turned into an empty result.
/// </summary>
public sealed class CountryScopedPipelineHost
{
    private readonly ICountrySettingSource _source;
    private readonly Func<CountrySettingObservation, DetectionPipeline> _factory;
    private readonly object _gate = new();
    private (CountrySettingObservation Observation, DetectionPipeline Pipeline)? _cached;
    private int _buildCount;

    public CountryScopedPipelineHost(ICountrySettingSource source, Func<CountrySettingObservation, DetectionPipeline> factory)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>How many pipelines have been built successfully (diagnostic).</summary>
    public int BuildCount => Volatile.Read(ref _buildCount);

    public DetectionPipeline Current()
    {
        lock (_gate)
        {
            var observation = _source.Read();
            if (!Enum.IsDefined(observation))
            {
                throw new ArgumentOutOfRangeException(nameof(observation), "The country setting observation is undefined.");
            }

            if (_cached is { } cached && cached.Observation == observation)
            {
                return cached.Pipeline;
            }

            _cached = null; // the pipeline for any other setting is never served again

            var pipeline = _factory(observation)
                ?? throw new InvalidOperationException("The pipeline factory returned no pipeline.");
            _cached = (observation, pipeline);
            Interlocked.Increment(ref _buildCount);
            return pipeline;
        }
    }
}
