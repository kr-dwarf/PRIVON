using Privon.Detection;
using Privon.UsSsn;

namespace Privon.UsProductIntegration;

// ISOLATED PRODUCT-INTEGRATION CANDIDATE -- NOT REGISTERED, NOT ACTIVATED. See UsCandidateFactory.cs.
//
// Each detector runs the core in TargetDetectorOnly mode: it reports ONLY its own type. Cross-type taxpayer-family
// arbitration (ATIN > ITIN > SSN) is a different mode and lives behind the product's IDetectorFamilyArbiter seam
// (UsTaxpayerFamilyArbiter), not inside these detectors. The two modes are never mixed.
//
// Identity, rank, readiness: literals, never inferred (DETECTOR_IDENTITY_IS_EXPLICIT). Readiness is Ready because none of
// the five depends on an authority snapshot; it is stated, not defaulted.

internal static class UsTaxpayerDetection
{
    public static IReadOnlyList<DetectionCandidate> Detect(
        DetectionContext context, UsTaxpayerKind kind, UsTaxpayerLabelSet labels,
        PiiType type, DetectorId id, int rank, string detectorName)
    {
        ArgumentNullException.ThrowIfNull(context);

        // An exception from the core is never caught here: a failed scan must not look like "no candidates".
        var result = UsTaxpayerFamily.EvaluateTargetDetectorOnly(context.View.RawText, kind, labels);

        var candidates = new List<DetectionCandidate>(result.Candidates.Count);
        foreach (var c in result.Candidates)
        {
            candidates.Add(UsCandidateFactory.Create(
                type, id, rank, detectorName, c.Span, c.Canonical.GetValueForProductCandidate(), UsCandidateFactory.EvidenceOf(c.Evidence)));
        }

        return candidates.AsReadOnly();
    }
}

public sealed class UsSsnCandidateDetector : IDetector
{
    private readonly UsTaxpayerLabelSet _labels;

    public UsSsnCandidateDetector() : this(UsTaxpayerLabelSet.Default) { }

    public UsSsnCandidateDetector(UsTaxpayerLabelSet labels) => _labels = labels ?? throw new ArgumentNullException(nameof(labels));

    public string Name => "UsSsnCandidateDetector";

    public PiiType PiiType => global::Privon.Detection.PiiType.UsSsn;

    public DetectorId Id { get; } = DetectorId.Create("us.ssn");

    public int ResolutionRank => 70;

    public DetectorReadinessState Readiness => DetectorReadinessState.Ready;

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context) =>
        UsTaxpayerDetection.Detect(context, UsTaxpayerKind.Ssn, _labels, PiiType, Id, ResolutionRank, Name);
}

public sealed class UsItinCandidateDetector : IDetector
{
    private readonly UsTaxpayerLabelSet _labels;

    public UsItinCandidateDetector() : this(UsTaxpayerLabelSet.Default) { }

    public UsItinCandidateDetector(UsTaxpayerLabelSet labels) => _labels = labels ?? throw new ArgumentNullException(nameof(labels));

    public string Name => "UsItinCandidateDetector";

    public PiiType PiiType => global::Privon.Detection.PiiType.UsItin;

    public DetectorId Id { get; } = DetectorId.Create("us.itin");

    public int ResolutionRank => 60;

    public DetectorReadinessState Readiness => DetectorReadinessState.Ready;

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context) =>
        UsTaxpayerDetection.Detect(context, UsTaxpayerKind.Itin, _labels, PiiType, Id, ResolutionRank, Name);
}

public sealed class UsAtinCandidateDetector : IDetector
{
    private readonly UsTaxpayerLabelSet _labels;

    public UsAtinCandidateDetector() : this(UsTaxpayerLabelSet.Default) { }

    public UsAtinCandidateDetector(UsTaxpayerLabelSet labels) => _labels = labels ?? throw new ArgumentNullException(nameof(labels));

    public string Name => "UsAtinCandidateDetector";

    public PiiType PiiType => global::Privon.Detection.PiiType.UsAtin;

    public DetectorId Id { get; } = DetectorId.Create("us.atin");

    public int ResolutionRank => 50;

    public DetectorReadinessState Readiness => DetectorReadinessState.Ready;

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context) =>
        UsTaxpayerDetection.Detect(context, UsTaxpayerKind.Atin, _labels, PiiType, Id, ResolutionRank, Name);
}
