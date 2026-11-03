using Privon.Detection;
using Privon.Detection.Family;
using Privon.Detection.Scope;
using Privon.UsSsn;

namespace Privon.UsProductIntegration;

// ISOLATED PRODUCT-INTEGRATION CANDIDATE -- NOT REGISTERED, NOT ACTIVATED. See UsCandidateFactory.cs.
//
// Supplies the U.S. implementations the product's explicit opt-in DetectionPipeline constructor asks for:
// the taxpayer family's attempt source (the product ships none) and, with the arbiter and provider already in
// UsTaxpayerFamilyArbitration.cs, everything needed to build that pipeline. The product (src) references none of this;
// the caller chooses to compose it.

/// <summary>The taxpayer family (ATIN / ITIN / SSN) as a product <see cref="IDetectorFamilyAttemptSource"/>.</summary>
public sealed class UsTaxpayerFamilyAttempts : IDetectorFamilyAttemptSource
{
    private static readonly IReadOnlyCollection<DetectorId> Members =
        Array.AsReadOnly([DetectorId.Create("us.atin"), DetectorId.Create("us.itin"), DetectorId.Create("us.ssn")]);

    private readonly UsTaxpayerLabelSet _labels;

    public UsTaxpayerFamilyAttempts() : this(UsTaxpayerLabelSet.Default) { }

    public UsTaxpayerFamilyAttempts(UsTaxpayerLabelSet labels) =>
        _labels = labels ?? throw new ArgumentNullException(nameof(labels));

    public string FamilyName => UsTaxpayerFamilyArbiter.TaxpayerFamilyName;

    public IReadOnlyCollection<DetectorId> MemberDetectorIds => Members;

    public IReadOnlyList<FamilyCandidateAttempt> CollectAttempts(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return UsTaxpayerFamilyAttemptSource.Collect(context.View.RawText, _labels);
    }
}

public static class UsOptInPipeline
{
    /// <summary>
    /// Builds a product <see cref="DetectionPipeline"/> through its explicit opt-in constructor: a real
    /// <see cref="DetectorRegistry"/> holding the U.S. detectors plus <paramref name="additionalDetectors"/>, the
    /// taxpayer family source, and the family arbiter provider. Returns a new object; registers and activates nothing.
    ///
    /// <paramref name="unitedStatesSetting"/> is what a trusted settings layer observed for the United States pack
    /// (null = no country scoping, the pack simply runs). Only <see cref="CountrySettingObservation.AuthenticatedOff"/>
    /// stops the U.S. detectors and the taxpayer family; every other value runs them and requires all required detectors and the
    /// taxpayer family to be present and Ready.
    /// </summary>
    public static DetectionPipeline Create(
        IReadOnlyList<IDetector>? additionalDetectors = null,
        IDetectorFamilyArbiterProvider? arbiterProvider = null,
        UsTaxpayerLabelSet? labels = null,
        CountrySettingObservation? unitedStatesSetting = null,
        Func<IReadOnlyList<IDetector>>? usDetectorFactory = null,
        UsAuthoritySet? authority = null) =>
        CreateWithRegistry(additionalDetectors, arbiterProvider, labels, unitedStatesSetting, usDetectorFactory, authority).Pipeline;

    /// <summary>
    /// The same pipeline <see cref="Create"/> builds, together with the registry it was built FROM -- the one fact the pipeline itself does not
    /// expose. A readiness statement read from this registry describes the detectors that actually run, never a separately kept count.
    /// </summary>
    public static (DetectionPipeline Pipeline, DetectorRegistry Registry) CreateWithRegistry(
        IReadOnlyList<IDetector>? additionalDetectors = null,
        IDetectorFamilyArbiterProvider? arbiterProvider = null,
        UsTaxpayerLabelSet? labels = null,
        CountrySettingObservation? unitedStatesSetting = null,
        Func<IReadOnlyList<IDetector>>? usDetectorFactory = null,
        UsAuthoritySet? authority = null)
    {
        var usDetectors = (usDetectorFactory ?? (() => UsIntegrationCandidates.CreateDetectors(authority)))();
        var detectors = new List<IDetector>(usDetectors);
        if (additionalDetectors is not null)
        {
            detectors.AddRange(additionalDetectors);
        }

        IReadOnlyList<IDetectorFamilyAttemptSource> sources = [new UsTaxpayerFamilyAttempts(labels ?? UsTaxpayerLabelSet.Default)];
        var provider = arbiterProvider ?? new UsTaxpayerFamilyArbiterProvider();
        var registry = DetectorRegistry.Create(detectors);

        if (unitedStatesSetting is null)
        {
            return (new DetectionPipeline(registry, provider, sources), registry);
        }

        return (new DetectionPipeline(registry, provider, sources, [UsScope(unitedStatesSetting.Value, authority)]), registry);
    }

    /// <summary>
    /// The U.S. pack scope: the eleven authority-independent detectors, the authority-dependent ones the given
    /// <paramref name="authority"/> backs, and the taxpayer family are what an active pack promises.
    /// </summary>
    public static CountryPackScope UsScope(CountrySettingObservation setting, UsAuthoritySet? authority = null) => new(
        CountryPack.UnitedStates,
        setting,
        UsIntegrationCandidates.RequiredDetectorIds(authority), // literals plus the validated authority, never the factory output (H-2)
        [UsTaxpayerFamilyArbiter.TaxpayerFamilyName]);
}
