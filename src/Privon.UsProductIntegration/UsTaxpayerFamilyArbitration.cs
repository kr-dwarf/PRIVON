using System.Diagnostics.CodeAnalysis;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Family;
using Privon.UsSsn;

namespace Privon.UsProductIntegration;

// ISOLATED PRODUCT-INTEGRATION CANDIDATE -- NOT REGISTERED, NOT ACTIVATED. See UsCandidateFactory.cs.
//
// The product defines the generic taxpayer-family arbitration SEAM (IDetectorFamilyArbiter / FamilyCandidateAttempt /
// DetectorFamilyArbitrationBoundary) and ships no arbiter ("the concrete arbiter lands at the taxpayer S3 transition").
// This is that arbiter, built on the core's own UsTaxpayerArbiter (ATIN > ITIN > SSN, overlapping spans mutually
// exclusive): no ordering rule is re-implemented here. The FAMILY NAME is a placeholder of this candidate: the contract
// does not name the taxpayer family, so it is an open decision (see the integration report).
//
// FAMILY_REJECTED_NEVER_EMITTED: a Rejected attempt never reaches Emitted. Input that cannot be arbitrated throws; it is
// never dropped, because that would report an incomplete arbitration as a complete one.

public sealed class UsTaxpayerFamilyArbiter : IDetectorFamilyArbiter
{
    public const string TaxpayerFamilyName = "us.taxpayer";

    /// <summary>The opaque rejection reason recorded for an accepted attempt that a higher-priority type superseded.</summary>
    public const string SupersededReason = "SUPERSEDED_BY_HIGHER_PRIORITY_TAXPAYER_TYPE";

    public string FamilyName => TaxpayerFamilyName;

    public FamilyArbitrationResult Arbitrate(IReadOnlyList<FamilyCandidateAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        var accepted = new List<UsTaxpayerCandidate>();
        var rejected = new List<FamilyCandidateAttempt>();

        foreach (var attempt in attempts)
        {
            ArgumentNullException.ThrowIfNull(attempt);
            var kind = KindOf(attempt.DetectorId);
            if (attempt.Level != RiskLevel.Level3)
            {
                throw new ArgumentException("A taxpayer-family attempt must be Level 3.", nameof(attempts));
            }

            switch (attempt.Outcome)
            {
                case FamilyAttemptOutcome.Rejected:
                    rejected.Add(attempt);
                    break;

                case FamilyAttemptOutcome.Accepted:
                    accepted.Add(ToCoreCandidate(attempt, kind));
                    break;

                default:
                    throw new ArgumentException("A taxpayer-family attempt carries an undefined outcome.", nameof(attempts));
            }
        }

        var (emitted, superseded) = UsTaxpayerArbiter.Resolve(accepted);

        foreach (var s in superseded)
        {
            var (type, id, _, _) = Describe(s.Kind);
            rejected.Add(new FamilyCandidateAttempt(
                id, RiskLevel.Level3, UsCandidateFactory.EvidenceOf(s.Evidence), s.Span,
                new CanonicalValue(type, s.Canonical.GetValueForProductCandidate()),
                FamilyAttemptOutcome.Rejected, SupersededReason));
        }

        var winners = new List<DetectionCandidate>(emitted.Count);
        foreach (var e in emitted)
        {
            var (type, id, rank, name) = Describe(e.Kind);
            winners.Add(UsCandidateFactory.Create(
                type, id, rank, name, e.Span, e.Canonical.GetValueForProductCandidate(), UsCandidateFactory.EvidenceOf(e.Evidence)));
        }

        // A deterministic, input-order-independent presentation of what was rejected.
        rejected.Sort(static (a, b) =>
        {
            int c = a.Span.Start.CompareTo(b.Span.Start);
            if (c != 0) return c;
            c = b.Span.Length.CompareTo(a.Span.Length);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.DetectorId.Value, b.DetectorId.Value);
            return c != 0 ? c : string.CompareOrdinal(a.RejectionReason, b.RejectionReason);
        });

        return new FamilyArbitrationResult(winners.AsReadOnly(), rejected.AsReadOnly());
    }

    private static UsTaxpayerCandidate ToCoreCandidate(FamilyCandidateAttempt attempt, UsTaxpayerKind kind)
    {
        var evidence = attempt.Evidence switch
        {
            ResolutionEvidence.S => UsSsnEvidence.Structure,
            ResolutionEvidence.SC => UsSsnEvidence.Structure | UsSsnEvidence.Context,
            _ => throw new ArgumentException("A taxpayer-family attempt carries evidence the cores never produce.", nameof(attempt)),
        };

        var (type, _, _, _) = Describe(kind);
        if (attempt.Canonical is not { } canonical || canonical.PiiType != type || string.IsNullOrEmpty(canonical.Value))
        {
            throw new ArgumentException("An accepted taxpayer-family attempt must carry its own type's canonical value.", nameof(attempt));
        }

        // The attempt contract has no display form; the form only breaks a tie between otherwise identical candidates.
        return new UsTaxpayerCandidate(kind, attempt.Span, evidence, UsSsnForm.Compact, new UsSsnCanonical(canonical.Value));
    }

    private static UsTaxpayerKind KindOf(DetectorId id) => id.HasValue
        ? id.Value switch
        {
            "us.atin" => UsTaxpayerKind.Atin,
            "us.itin" => UsTaxpayerKind.Itin,
            "us.ssn" => UsTaxpayerKind.Ssn,
            _ => throw new ArgumentException("A taxpayer-family attempt carries a detector id outside the taxpayer family.", "attempts"),
        }
        : throw new ArgumentException("A taxpayer-family attempt carries no detector id.", "attempts");

    internal static (PiiType Type, DetectorId Id, int Rank, string Name) Describe(UsTaxpayerKind kind) => kind switch
    {
        UsTaxpayerKind.Atin => (PiiType.UsAtin, DetectorId.Create("us.atin"), 50, "UsAtinCandidateDetector"),
        UsTaxpayerKind.Itin => (PiiType.UsItin, DetectorId.Create("us.itin"), 60, "UsItinCandidateDetector"),
        UsTaxpayerKind.Ssn => (PiiType.UsSsn, DetectorId.Create("us.ssn"), 70, "UsSsnCandidateDetector"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public sealed class UsTaxpayerFamilyArbiterProvider : IDetectorFamilyArbiterProvider
{
    public bool TryGetArbiter(string familyName, [MaybeNullWhen(false)] out IDetectorFamilyArbiter arbiter)
    {
        ArgumentNullException.ThrowIfNull(familyName);

        if (string.Equals(familyName, UsTaxpayerFamilyArbiter.TaxpayerFamilyName, StringComparison.Ordinal))
        {
            arbiter = new UsTaxpayerFamilyArbiter();
            return true;
        }

        arbiter = null;
        return false;
    }
}

/// <summary>
/// Produces the per-type attempts the family seam consumes (the product detectors yield candidates only). The core is
/// evaluated in TaxFamilyResolver mode, so the attempts reflect exactly the family semantics, including its field
/// ownership behaviour (policy O-1, unchanged here): an emitted candidate becomes an Accepted attempt carrying its value,
/// every rejected or superseded verdict becomes a Rejected attempt carrying only its opaque reason.
///
/// This is deliberately NOT built from the three TargetDetectorOnly runs: those modes differ for a number that sits inside
/// a field labelled for another taxpayer type, and mixing them would silently change what the family reports.
/// </summary>
public static class UsTaxpayerFamilyAttemptSource
{
    public static IReadOnlyList<FamilyCandidateAttempt> Collect(string? rawText) =>
        Collect(rawText, UsTaxpayerLabelSet.Default);

    public static IReadOnlyList<FamilyCandidateAttempt> Collect(string? rawText, UsTaxpayerLabelSet labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        var result = UsTaxpayerFamily.EvaluateTaxFamilyResolver(rawText, labels);
        var attempts = new List<FamilyCandidateAttempt>(result.Candidates.Count + result.Attempts.Count);

        foreach (var c in result.Candidates)
        {
            var (type, id, _, _) = UsTaxpayerFamilyArbiter.Describe(c.Kind);
            attempts.Add(new FamilyCandidateAttempt(
                id, RiskLevel.Level3, UsCandidateFactory.EvidenceOf(c.Evidence), c.Span,
                new CanonicalValue(type, c.Canonical.GetValueForProductCandidate()), FamilyAttemptOutcome.Accepted, null));
        }

        foreach (var a in result.Attempts.Where(a => a.Outcome != UsTaxpayerAttemptOutcome.Accepted))
        {
            var (_, id, _, _) = UsTaxpayerFamilyArbiter.Describe(a.Kind);
            attempts.Add(new FamilyCandidateAttempt(
                id, RiskLevel.Level3, UsCandidateFactory.EvidenceOf(a.Evidence), a.Span, null,
                FamilyAttemptOutcome.Rejected, a.Reason?.ToString() ?? a.Outcome.ToString()));
        }

        return attempts.AsReadOnly();
    }
}
