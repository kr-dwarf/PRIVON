using System.Runtime.CompilerServices;
using Privon.Detection;
using Privon.Detection.Authority;
using Privon.Detection.Detectors;
using Privon.Detection.Scope;

namespace Privon.UsProductIntegration;

// ISOLATED PRODUCT-INTEGRATION LAYER -- NOT REGISTERED, NOT ACTIVATED. See UsCandidateFactory.cs.
//
// THE OWNER OF THE EXPLICIT U.S. OPT-IN. It is the single object that ties the pieces together, and it states only what it can read from them:
//
//   an EXPLICITLY chosen UsAuthoritySet (UsAuthoritySet.Production for the verified official artifact; anything else is labelled as such)
//     -> the opt-in pipeline built for the CURRENT country setting (UsOptInPipeline, with its registry)
//     -> the real CountryScopedPipelineHost that serves it (and rebuilds it when the setting changes)
//     -> a readiness snapshot read from the registry of THAT pipeline -- never from a separately kept count.
//
// WHAT IT DOES NOT DO. It does not activate anything by itself: the shipped app only ever reaches it through UsGatedOptInOwner, which serves it only
// after an explicit, verified user activation (UsOptInGate). It does not authenticate the country setting: it follows whatever ICountrySettingSource says, and the
// source the product has can never say "authenticated off" (see CountrySettingSources.cs) -- so T-1 is neither created nor claimed here. It
// does not block a paste or a send. "Ready" means a detector can run; it is not protection of anything.
//
// The snapshot uses only types of this assembly. A host that owns both this assembly and the app's internals maps it to the tray's report
// (the app's PrivonAppComposition takes a pipeline delegate and a readiness delegate); that mapping is the host's, not this assembly's.

/// <summary>Where the authority behind the phone / address detectors came from.</summary>
public enum UsOptInProvenance
{
    NotSelected = 0,

    /// <summary>The official-source production artifact, checked against the full production scope.</summary>
    Production,

    /// <summary>A caller-supplied authority checked for shape only (tests, fixtures): NOT production data.</summary>
    Provided,
}

/// <summary>Why a detector can or cannot run. <see cref="NotProvided"/> is the zero value: nothing is ready unless the registry says so.</summary>
public enum UsOptInAvailability
{
    NotProvided = 0,
    ValidationFailed,
    Ready,
}

public sealed record UsOptInDetectorStatus(string DetectorId, UsOptInAvailability Availability);

/// <summary>What the pipeline in use states about the thirteen U.S. detectors right now. Detector ids and enums only -- no text of any kind.</summary>
public sealed record UsOptInReadinessSnapshot(
    bool UsEnabled, UsOptInProvenance Provenance, IReadOnlyList<UsOptInDetectorStatus> Detectors, string? ArtifactVersion,
    UsOptInGate Gate = UsOptInGate.NotGated);

public sealed class UsOptInOwner : IDisposable
{
    private sealed record Build(CountrySettingObservation Observation, IReadOnlySet<string> RegisteredIds);

    private readonly CountryScopedPipelineHost _host;
    private readonly UsAuthoritySet _authority;
    private readonly ConditionalWeakTable<DetectionPipeline, Build> _builds = new();
    private int _disposed;

    private UsOptInOwner(ICountrySettingSource source, UsAuthoritySet authority, IReadOnlyList<IDetector> additionalDetectors)
    {
        _authority = authority;
        _host = new CountryScopedPipelineHost(source, setting =>
        {
            var (pipeline, registry) = UsOptInPipeline.CreateWithRegistry(
                additionalDetectors, unitedStatesSetting: setting, authority: authority);
            _builds.Add(pipeline, new Build(setting, registry.All.Select(r => r.Id.Value).ToHashSet(StringComparer.Ordinal)));
            return pipeline;
        });
    }

    /// <summary>
    /// The explicit selection of the verified official-source authority (<see cref="UsAuthoritySet.Production"/>): the only way to obtain an owner whose
    /// 13/13 can be presented as production-verified. <paramref name="additionalDetectors"/> defaults to the nine detectors the shipped default
    /// pipeline runs, so Korean and global detection is the shipped one.
    /// </summary>
    public static UsOptInOwner CreateWithProductionAuthority(ICountrySettingSource source, IReadOnlyList<IDetector>? additionalDetectors = null) =>
        Create(source, UsAuthoritySet.Production, additionalDetectors);

    /// <summary>An owner over any authority the caller chose (none, a shape-only fixture, a damaged one). It is labelled by that authority's own origin.</summary>
    public static UsOptInOwner Create(ICountrySettingSource source, UsAuthoritySet? authority, IReadOnlyList<IDetector>? additionalDetectors = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new UsOptInOwner(source, authority ?? UsAuthoritySet.Unavailable, additionalDetectors ?? ShippedDetectors());
    }

    /// <summary>The nine detectors <c>DetectionPipeline.CreateDefault()</c> runs (a test pins that they behave identically).</summary>
    public static IReadOnlyList<IDetector> ShippedDetectors() =>
    [
        new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
        new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(), new GpsCoordinateDetector(),
    ];

    /// <summary>The pipeline for the CURRENT country setting, from the real host (cached per setting, rebuilt when it changes). Throws if it cannot be built.</summary>
    public DetectionPipeline CurrentPipeline()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _host.Current();
    }

    /// <summary>How many times the host built a pipeline (a rebuild happens only when the setting changed).</summary>
    public int BuildCount => _host.BuildCount;

    /// <summary>
    /// The readiness of the pipeline that is serving scans RIGHT NOW: the host is asked for its current pipeline, and the registry that very instance
    /// was built from says which detectors exist. A detector with no registration is not ready; why is read from the authority's own states.
    /// Throws if the pipeline cannot be built, so a failure is never reported as an old "ready".
    /// </summary>
    public UsOptInReadinessSnapshot CurrentReadiness()
    {
        var pipeline = CurrentPipeline();
        if (!_builds.TryGetValue(pipeline, out var build))
        {
            throw new InvalidOperationException("The pipeline in use was not built by this owner.");
        }

        var detectors = UsIntegrationCandidates.ApprovedDetectorIds.Concat(UsIntegrationCandidates.AuthorityDependentDetectorIds)
            .Select(id => new UsOptInDetectorStatus(
                id.Value, build.RegisteredIds.Contains(id.Value) ? UsOptInAvailability.Ready : WhyNotReady(id)))
            .ToList()
            .AsReadOnly();
        var provenance = _authority.Origin switch
        {
            UsAuthorityOrigin.ProductionScope => UsOptInProvenance.Production,
            UsAuthorityOrigin.Provided => UsOptInProvenance.Provided,
            _ => UsOptInProvenance.NotSelected,
        };
        bool allReady = detectors.All(d => d.Availability == UsOptInAvailability.Ready);

        return new UsOptInReadinessSnapshot(
            UsEnabled: build.Observation != CountrySettingObservation.AuthenticatedOff,   // exactly what the pack scope follows; no authenticity is claimed
            provenance,
            detectors,
            provenance == UsOptInProvenance.Production && allReady ? new UsProductionAuthorityProvider().ArtifactVersion : null);
    }

    // "Not selected" when the data was never supplied; "validation failed" when something was supplied and was refused (a damaged snapshot, a
    // hash or version mismatch, a duplicate, or valid data that does not cover the production scope).
    private UsOptInAvailability WhyNotReady(DetectorId id)
    {
        var states = id.Value == "us.phone"
            ? new[] { _authority.NpaState }
            : id.Value == "us.address" ? new[] { _authority.SuffixState, _authority.StateState } : [];
        if (states.Length == 0) return UsOptInAvailability.ValidationFailed;   // a literal detector missing from the registry is a fault, not a choice

        bool onlyMissing = states.All(s => s == AuthoritySnapshotState.SnapshotMissing);
        bool anyBad = states.Any(s => s is not (AuthoritySnapshotState.SnapshotMissing or AuthoritySnapshotState.SnapshotValid));
        bool validButUnfit = states.All(s => s != AuthoritySnapshotState.SnapshotMissing);
        return onlyMissing || (!anyBad && !validButUnfit) ? UsOptInAvailability.NotProvided : UsOptInAvailability.ValidationFailed;
    }

    /// <summary>After disposal the owner serves no pipeline and no readiness: every call throws, so nothing it said earlier can be mistaken for the present.</summary>
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
