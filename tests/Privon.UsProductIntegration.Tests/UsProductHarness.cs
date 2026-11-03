using Privon.Core;
using Privon.Detection;
using Privon.UsProductIntegration;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY. Shared helpers that drive the REAL product types (IDetector, DetectionContext, NormalizedView,
// DetectionCandidate, DetectorRegistry, DetectionPipeline, CandidateResolver, AliasMap ...). Nothing here is a fake of a
// product type.
internal static class UsProductHarness
{
    public static IDetector[] AllFive() =>
    [
        new UsAtinCandidateDetector(), new UsItinCandidateDetector(), new UsSsnCandidateDetector(),
        new UsMbiCandidateDetector(), new UsSevisCandidateDetector(),
    ];

    /// <summary>The five plus the passport, A-Number and I-94 detectors.</summary>
    public static IDetector[] AllEight() =>
    [
        .. AllFive(), new UsPassportCandidateDetector(), new UsANumberCandidateDetector(), new UsI94CandidateDetector(),
    ];

    public static IReadOnlyList<DetectionCandidate> Run(IDetector detector, string raw) =>
        detector.Detect(new DetectionContext(NormalizedView.Build(raw)));

    public static List<DetectionCandidate> RunAll(string raw) =>
        AllFive().SelectMany(d => Run(d, raw)).ToList();

    /// <summary>The frozen D3A oracle candidate record: id|type|level|evidence|start:length|canonical|alias|rank.</summary>
    public static string Oracle(DetectionCandidate c)
    {
        string level = c.RiskLevel switch { RiskLevel.Level3 => "L3", RiskLevel.Level2 => "L2", _ => "L1" };
        string evidence = c.Evidence switch
        {
            ResolutionEvidence.S => "S",
            ResolutionEvidence.SC => "S+C",
            ResolutionEvidence.C => "C",
            ResolutionEvidence.SJP => "S+J",
            ResolutionEvidence.CJP => "C+J",
            ResolutionEvidence.SCJP => "S+C+J",
            _ => "?",
        };
        if (c.PiiType == PiiType.BankAccount && c.Evidence == ResolutionEvidence.SCJP)
        {
            evidence = "S+C+P"; // the frozen D3A spelling of the paired-corroboration evidence for the bank tuple
        }

        string alias = new AliasIdentity(AliasFamilyTable.FamilyOf(c.PiiType), 1).CanonicalContractToken;
        return $"{c.DetectorId.Value}|{c.PiiType}|{level}|{evidence}|{c.Span.Start}:{c.Span.Length}|{c.Canonical!.Value.Value}|{alias}|{c.ResolutionRank}";
    }

    /// <summary>The oracle record plus the passport's ownerClaim, written the way the frozen D3A record writes it.</summary>
    public static string OracleWithOwner(DetectionCandidate c) => c.OwnerClaim switch
    {
        OwnerClaim.True => Oracle(c) + "|ownerClaim=true",
        OwnerClaim.False => Oracle(c) + "|ownerClaim=false",
        _ => Oracle(c),
    };

    public static string Slice(string raw, DetectionCandidate c) => raw.Substring(c.Span.Start, c.Span.Length);
}
