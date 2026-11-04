using Privon.Core;
using Privon.Detection;
using Privon.UsMbiSevis;
using Privon.UsSsn;

namespace Privon.UsProductIntegration;

// ISOLATED PRODUCT-INTEGRATION CANDIDATE -- NOT REGISTERED, NOT ACTIVATED, NOT AN OFFICIAL DETECTOR.
// Adapts the isolated, non-operative U.S. cores (Privon.UsSsn, Privon.UsMbiSevis) to the REAL
// product contracts: IDetector, DetectionCandidate, DetectorId, ResolutionEvidence, CanonicalValue and the
// IDetectorFamilyArbiter seam. It re-implements no grammar, range, checksum or arbitration rule: every judgement comes
// from the cores. Nothing here is added to DetectorRegistry or DetectionPipeline, and no production file refers to it.

/// <summary>The single place a core result becomes a product <see cref="DetectionCandidate"/>.</summary>
internal static class UsCandidateFactory
{
    // Every one of the five identifiers is Level 3 (D2.2); structural validation is what the cores establish, so the
    // detector-confidence signal is High. Confidence is not read by the contract-domain resolver (see CandidateResolver).
    public static DetectionCandidate Create(
        PiiType type, DetectorId id, int rank, string detectorName, RawSpan span, string canonicalValue, ResolutionEvidence evidence,
        OwnerClaim ownerClaim = OwnerClaim.NotApplicable,
        RiskLevel level = RiskLevel.Level3,
        DetectionConfidence confidence = DetectionConfidence.High) =>
        new(
            type,
            span,
            level,
            confidence,
            new CanonicalValue(type, canonicalValue),
            detectorName,
            // DETECTOR_MUST_STAMP_IDENTITY (A.9 section 7.1): identity, rank and evidence are stated explicitly. The
            // AliasIdentity stays null (downstream assigns it). OwnerClaim stays NotApplicable except for the passport, the
            // only one of these identifiers whose contract states a jurisdiction (ownership) claim.
            DetectorId: id,
            Evidence: evidence,
            ResolutionRank: rank,
            OwnerClaim: ownerClaim);

    public static ResolutionEvidence EvidenceOf(UsSsnEvidence evidence) => evidence switch
    {
        UsSsnEvidence.Structure => ResolutionEvidence.S,
        UsSsnEvidence.Structure | UsSsnEvidence.Context => ResolutionEvidence.SC,
        _ => throw new InvalidOperationException("A core produced evidence outside the frozen S / S+C set."),
    };

    // The frozen evidence tuples of the passport (S+C+J, S+C, S+J, C+J, C), A-Number and I-94 (S+C). J alone is never produced.
    public static ResolutionEvidence EvidenceOf(UsDocumentEvidence evidence) => evidence switch
    {
        UsDocumentEvidence.Structure => ResolutionEvidence.S,
        UsDocumentEvidence.Context => ResolutionEvidence.C,
        UsDocumentEvidence.Structure | UsDocumentEvidence.Context => ResolutionEvidence.SC,
        UsDocumentEvidence.Structure | UsDocumentEvidence.Jurisdiction => ResolutionEvidence.SJP,
        UsDocumentEvidence.Context | UsDocumentEvidence.Jurisdiction => ResolutionEvidence.CJP,
        UsDocumentEvidence.Structure | UsDocumentEvidence.Context | UsDocumentEvidence.Jurisdiction => ResolutionEvidence.SCJP,
        _ => throw new InvalidOperationException("A core produced evidence outside the frozen document-identifier set."),
    };

    public static ResolutionEvidence EvidenceOf(UsIdEvidence evidence) => evidence switch
    {
        UsIdEvidence.Structure => ResolutionEvidence.S,
        UsIdEvidence.Structure | UsIdEvidence.Context => ResolutionEvidence.SC,
        _ => throw new InvalidOperationException("A core produced evidence outside the frozen S / S+C set."),
    };
}
