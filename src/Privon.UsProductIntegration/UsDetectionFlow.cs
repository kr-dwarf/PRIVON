using Privon.Detection;
using Privon.Detection.Family;
using Privon.UsSsn;

namespace Privon.UsProductIntegration;

// ISOLATED PRODUCT-INTEGRATION CANDIDATE -- NOT REGISTERED, NOT ACTIVATED. See UsCandidateFactory.cs.
//
// The assembled execution flow for the U.S. identifiers, built only from existing product stages:
//
//   MBI / SEVIS / passport / A-Number / I-94 / driver ID / bank detectors  --------------------------------------------+
//   taxpayer attempts (TaxFamilyResolver semantics, O-1 unchanged)     |
//        -> DetectorFamilyArbitrationBoundary (the real seam)  --------+--> CandidateResolver (R0..R6) -> ContextRiskAdjuster
//
// What the product's own DetectionPipeline does today is different: it runs OverlapResolver (level, confidence, length)
// and never calls the family boundary or CandidateResolver. Wiring this order into DetectionPipeline is a production
// change and is NOT made here; this class is the executable candidate for that change.
//
// The three taxpayer detectors (UsSsn/UsItin/UsAtin CandidateDetector) are TargetDetectorOnly and are deliberately NOT part
// of this flow: their union differs from the family mode where a number sits inside a field labelled for another taxpayer
// type (policy O-1), and their output must never be final before family arbitration.
//
// Failure is never reported as "no candidates": no arbiter for the family, an arbiter returning nothing, an invalid span,
// or any detector failure all surface as exceptions.

public sealed class UsFamilyArbitrationUnavailableException : InvalidOperationException
{
    public UsFamilyArbitrationUnavailableException()
        : base("The taxpayer-family arbiter is unavailable; the scan cannot be reported as complete.")
    {
    }
}

public sealed class UsDetectionFlow
{
    private static readonly HashSet<string> TaxpayerIds = new(StringComparer.Ordinal) { "us.atin", "us.itin", "us.ssn" };

    private readonly IDetectorFamilyArbiterProvider _provider;
    private readonly UsTaxpayerLabelSet _labels;
    private readonly IReadOnlyList<IDetector> _detectors;

    public UsDetectionFlow()
        : this(new UsTaxpayerFamilyArbiterProvider(), Array.Empty<IDetector>(), UsTaxpayerLabelSet.Default)
    {
    }

    /// <param name="provider">Resolves the taxpayer-family arbiter through the product's real seam.</param>
    /// <param name="additionalDetectors">Other detectors that run in the same flow (for example Korean or Global ones).
    /// The individual taxpayer detectors are refused: their output is only ever final through family arbitration.</param>
    /// <param name="labels">The recognized-label set handed to the taxpayer family.</param>
    public UsDetectionFlow(
        IDetectorFamilyArbiterProvider provider, IReadOnlyList<IDetector> additionalDetectors, UsTaxpayerLabelSet labels,
        UsAuthoritySet? authority = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(additionalDetectors);
        ArgumentNullException.ThrowIfNull(labels);

        foreach (var detector in additionalDetectors)
        {
            ArgumentNullException.ThrowIfNull(detector);
            if (detector.Id.HasValue && TaxpayerIds.Contains(detector.Id.Value))
            {
                throw new ArgumentException(
                    "A taxpayer-family detector cannot join the flow directly; its result is final only through family arbitration.",
                    nameof(additionalDetectors));
            }
        }

        _provider = provider;
        _labels = labels;
        _detectors =
        [
            new UsMbiCandidateDetector(), new UsSevisCandidateDetector(),
            new UsPassportCandidateDetector(), new UsANumberCandidateDetector(), new UsI94CandidateDetector(),
            new UsDriverStateIdCandidateDetector(), new UsBankAccountCandidateDetector(), new UsNameCandidateDetector(),
            .. AuthorityDependent(authority ?? UsAuthoritySet.Unavailable),
            .. additionalDetectors,
        ];
    }

    private static IEnumerable<IDetector> AuthorityDependent(UsAuthoritySet set)
    {
        if (set.PhoneReady) yield return new UsPhoneCandidateDetector(set);
        if (set.AddressReady) yield return new UsAddressCandidateDetector(set);
    }

    public DetectionResult Detect(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);
        var collected = new List<DetectionCandidate>();

        // 1. Taxpayer family: attempts in family-mode semantics -> the product's real arbitration boundary. The boundary is
        //    always consulted, even for text with no taxpayer content, so an unavailable arbiter is always observed.
        var attempts = UsTaxpayerFamilyAttemptSource.Collect(rawText, _labels);
        if (!DetectorFamilyArbitrationBoundary.TryArbitrate(
                _provider, UsTaxpayerFamilyArbiter.TaxpayerFamilyName, attempts, out var arbitration, out _))
        {
            throw new UsFamilyArbitrationUnavailableException();
        }

        // FAMILY_REJECTED_NEVER_EMITTED: only the arbiter's Emitted list becomes candidates.
        collected.AddRange(arbitration!.Emitted);

        // 2. The remaining detectors (an exception from any of them propagates).
        foreach (var detector in _detectors)
        {
            collected.AddRange(detector.Detect(context));
        }

        // 3. The product's general resolver, strictly after family arbitration. It validates spans and value domains
        //    fail-closed, collapses exact duplicates and resolves overlaps by the frozen rank/evidence order.
        var resolved = CandidateResolver.Resolve(new ResolutionInput(collected, rawText.Length));

        // 4. The same post-resolution stage DetectionPipeline applies (Level 3 is never touched by it).
        return new DetectionResult(ContextRiskAdjuster.Adjust(view, resolved.Winners));
    }
}
