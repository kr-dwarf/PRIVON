using Privon.Detection;
using Privon.UsMbiSevis;

namespace Privon.UsProductIntegration;

// ISOLATED PRODUCT-INTEGRATION CANDIDATE -- NOT REGISTERED, NOT ACTIVATED. See UsCandidateFactory.cs.
// The label set of other fields is an input (the shared FieldBinder is grammar-free); the strong labels are the cores'
// own defaults. An exception from a core is never caught: a failed scan must not look like "no candidates".

public sealed class UsMbiCandidateDetector : IDetector
{
    private readonly IReadOnlyList<string> _otherLabels;

    public UsMbiCandidateDetector() : this(Array.Empty<string>()) { }

    /// <param name="otherRecognizedLabels">Labels of other fields; they only terminate an MBI field's value.</param>
    public UsMbiCandidateDetector(IReadOnlyList<string> otherRecognizedLabels) =>
        _otherLabels = otherRecognizedLabels ?? throw new ArgumentNullException(nameof(otherRecognizedLabels));

    public string Name => "UsMbiCandidateDetector";

    public PiiType PiiType => global::Privon.Detection.PiiType.UsMbi;

    public DetectorId Id { get; } = DetectorId.Create("us.mbi");

    public int ResolutionRank => 80;

    public DetectorReadinessState Readiness => DetectorReadinessState.Ready;

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var result = UsMbiScanner.Detect(context.View.RawText, UsMbiScanner.DefaultStrongLabels, _otherLabels);

        var candidates = new List<DetectionCandidate>(result.Candidates.Count);
        foreach (var c in result.Candidates)
        {
            candidates.Add(UsCandidateFactory.Create(
                PiiType, Id, ResolutionRank, Name, c.Span, c.Canonical.GetValueForProductCandidate(), UsCandidateFactory.EvidenceOf(c.Evidence)));
        }

        return candidates.AsReadOnly();
    }
}

public sealed class UsSevisCandidateDetector : IDetector
{
    private readonly IReadOnlyList<string> _otherLabels;

    public UsSevisCandidateDetector() : this(Array.Empty<string>()) { }

    /// <param name="otherRecognizedLabels">Labels of other fields; they only terminate a SEVIS field's value.</param>
    public UsSevisCandidateDetector(IReadOnlyList<string> otherRecognizedLabels) =>
        _otherLabels = otherRecognizedLabels ?? throw new ArgumentNullException(nameof(otherRecognizedLabels));

    public string Name => "UsSevisCandidateDetector";

    public PiiType PiiType => global::Privon.Detection.PiiType.UsSevis;

    public DetectorId Id { get; } = DetectorId.Create("us.sevis");

    public int ResolutionRank => 90;

    public DetectorReadinessState Readiness => DetectorReadinessState.Ready;

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var result = UsSevisScanner.Detect(context.View.RawText, UsSevisScanner.DefaultStrongLabels, _otherLabels);

        var candidates = new List<DetectionCandidate>(result.Candidates.Count);
        foreach (var c in result.Candidates)
        {
            candidates.Add(UsCandidateFactory.Create(
                PiiType, Id, ResolutionRank, Name, c.Span, c.Canonical.GetValueForProductCandidate(), UsCandidateFactory.EvidenceOf(c.Evidence)));
        }

        return candidates.AsReadOnly();
    }
}

/// <summary>Builds the candidate detectors for a caller that chooses to put them somewhere. Registers nothing.</summary>
public static class UsIntegrationCandidates
{
    /// <summary>
    /// The eleven approved identities that need no external authority data, as LITERALS. Deliberately independent of
    /// <see cref="CreateDetectors"/>: a pack scope built from the factory output would shrink its own requirement whenever the
    /// factory dropped a detector and report a smaller scan as healthy. The two authority-dependent identities
    /// (<see cref="AuthorityDependentDetectorIds"/>) are required only while a VALID <see cref="UsAuthoritySet"/> backs them.
    /// </summary>
    public static IReadOnlyList<DetectorId> ApprovedDetectorIds { get; } = Array.AsReadOnly(
    [
        DetectorId.Create("us.atin"), DetectorId.Create("us.itin"), DetectorId.Create("us.ssn"),
        DetectorId.Create("us.mbi"), DetectorId.Create("us.sevis"),
        DetectorId.Create("us.passport"), DetectorId.Create("us.a_number"), DetectorId.Create("us.i94"),
        DetectorId.Create("us.driver_state_id"), DetectorId.Create("us.bank_account"),
        DetectorId.Create("us.name"),
    ]);

    /// <summary>The identities whose detectors depend on authority snapshots: phone (NPA) and address (USPS suffix + state).</summary>
    public static IReadOnlyList<DetectorId> AuthorityDependentDetectorIds { get; } = Array.AsReadOnly(
        [DetectorId.Create("us.phone"), DetectorId.Create("us.address")]);

    /// <summary>
    /// The detectors for a scan. Phone and address are included ONLY when <paramref name="authority"/> can back them: a detector
    /// that is UnavailableAuthoritySnapshot is not registered, because the opt-in pipeline requires every registered detector to
    /// be Ready, and D2.2 wants such a detector to emit nothing while the others carry on. Which of the thirteen are ready is
    /// <see cref="ReadinessReport"/>.
    /// </summary>
    public static IReadOnlyList<IDetector> CreateDetectors(UsAuthoritySet? authority = null)
    {
        var set = authority ?? UsAuthoritySet.Unavailable;
        var detectors = new List<IDetector>
        {
            new UsAtinCandidateDetector(), new UsItinCandidateDetector(), new UsSsnCandidateDetector(),
            new UsMbiCandidateDetector(), new UsSevisCandidateDetector(),
            new UsPassportCandidateDetector(), new UsANumberCandidateDetector(), new UsI94CandidateDetector(),
            new UsDriverStateIdCandidateDetector(), new UsBankAccountCandidateDetector(),
            new UsNameCandidateDetector(),
        };

        if (set.PhoneReady)
        {
            detectors.Add(new UsPhoneCandidateDetector(set));
        }

        if (set.AddressReady)
        {
            detectors.Add(new UsAddressCandidateDetector(set));
        }

        return detectors.AsReadOnly();
    }

    /// <summary>The required identities for a scan: the eleven literals plus each authority-dependent one the authority backs.</summary>
    public static IReadOnlyList<DetectorId> RequiredDetectorIds(UsAuthoritySet? authority = null)
    {
        var set = authority ?? UsAuthoritySet.Unavailable;
        var required = new List<DetectorId>(ApprovedDetectorIds);
        if (set.PhoneReady)
        {
            required.Add(DetectorId.Create("us.phone"));
        }

        if (set.AddressReady)
        {
            required.Add(DetectorId.Create("us.address"));
        }

        return required.AsReadOnly();
    }

    /// <summary>The thirteen approved identities and whether a detector for each is ready with <paramref name="authority"/>.</summary>
    public static IReadOnlyList<(DetectorId Id, DetectorReadinessState Readiness)> ReadinessReport(UsAuthoritySet? authority = null)
    {
        var ready = CreateDetectors(authority).Select(d => d.Id.Value).ToHashSet(StringComparer.Ordinal);
        return ApprovedDetectorIds.Concat(AuthorityDependentDetectorIds)
            .Select(id => (id, ready.Contains(id.Value) ? DetectorReadinessState.Ready : DetectorReadinessState.UnavailableAuthoritySnapshot))
            .ToList()
            .AsReadOnly();
    }
}
