using Privon.UsMbiSevis;

namespace Privon.UsMbiSevis.Tests;

// SYNTHETIC DATA ONLY. These cases replicate, verbatim, the MBI/SEVIS raw inputs and expected outcomes of the FROZEN
// D3A corpus (read-only; the corpus files are not modified). CaseIds are cited for traceability only. Only the
// TargetDetectorOnly / DetectorPipeline-candidate expectations apply to this module; ResolverHarness and policy
// fixtures belong to the resolver/pipeline and are out of scope here.
// EXPERIMENT ONLY: this module is not an official detector, is not registered, and is not a security determination.
public class UsMbiSevisFrozenFixtureTests
{
    // Mirror of the frozen oracle's candidate record, minus the alias token/rank, which belong to the pipeline.
    private static string Evidence(UsIdEvidence e) =>
        e == (UsIdEvidence.Structure | UsIdEvidence.Context) ? "S+C" : e == UsIdEvidence.Structure ? "S" : "?";

    private static string Oracle(UsMbiCandidate c) =>
        $"us.mbi|UsMbi|L3|{Evidence(c.Evidence)}|{c.Span.Start}:{c.Span.Length}|{c.Canonical.GetValueForTestingOnly()}";

    private static string Oracle(UsSevisCandidate c) =>
        $"us.sevis|UsSevis|L3|{Evidence(c.Evidence)}|{c.Span.Start}:{c.Span.Length}|{c.Canonical.GetValueForTestingOnly()}";

    private static string OracleReason(UsMbiRejection r) => r.Reason switch
    {
        UsMbiRejectionReason.Position1RequiresOneToNine => "POSITION1_REQUIRES_1_9",
        UsMbiRejectionReason.PositionRequiresAllowedLetter => $"POSITION{r.Position}_REQUIRES_ALLOWED_LETTER",
        UsMbiRejectionReason.PositionRequiresDigit => $"POSITION{r.Position}_REQUIRES_DIGIT",
        UsMbiRejectionReason.ExcludedB => "EXCLUDED_B",
        UsMbiRejectionReason.ExcludedI => "EXCLUDED_I",
        UsMbiRejectionReason.ExcludedL => "EXCLUDED_L",
        UsMbiRejectionReason.ExcludedO => "EXCLUDED_O",
        UsMbiRejectionReason.ExcludedS => "EXCLUDED_S",
        UsMbiRejectionReason.ExcludedZ => "EXCLUDED_Z",
        UsMbiRejectionReason.WrongLength => "WRONG_LENGTH",
        UsMbiRejectionReason.BoundaryViolation => "BOUNDARY_VIOLATION",
        UsMbiRejectionReason.NonAsciiLookalike => "NON_ASCII_LOOKALIKE",
        UsMbiRejectionReason.BadGrouping => "GROUPING_MUST_BE_4_3_4",
        UsMbiRejectionReason.MixedSeparators => "GROUPING_SEPARATOR_MUST_BE_CONSISTENT",
        _ => "NOT_A_FROZEN_ORACLE_REASON",
    };

    private static string OracleReason(UsSevisRejection r) => r.Reason switch
    {
        UsSevisRejectionReason.WrongLength => "WRONG_LENGTH_10_DIGITS_REQUIRED",
        UsSevisRejectionReason.PrefixMustBeN => "PREFIX_MUST_BE_N",
        UsSevisRejectionReason.NonAsciiDigit => "NON_ASCII_DIGIT",
        UsSevisRejectionReason.BoundaryViolation => "ALPHANUMERIC_BOUNDARY_VIOLATION",
        _ => "NOT_A_FROZEN_ORACLE_REASON",
    };

    // ---- MBI ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("US-MBI-P01", "MBI: 1A23C45DE67", "us.mbi|UsMbi|L3|S+C|5:11|1A23C45DE67")]
    [InlineData("US-MBI-G01", "MBI: 1A23-C45-DE67", "us.mbi|UsMbi|L3|S+C|5:13|1A23C45DE67")]
    [InlineData("US-MBI-G02", "MBI: 1A23 C45 DE67", "us.mbi|UsMbi|L3|S+C|5:13|1A23C45DE67")]
    [InlineData("US-MBI-LOWER", "MBI: 1a23c45de67", "us.mbi|UsMbi|L3|S+C|5:11|1A23C45DE67")]
    public void FrozenMbiPositives_ProduceExactlyTheFrozenCandidate(string caseId, string raw, string expected)
    {
        var result = UsMbiScanner.Detect(raw);

        Assert.True(result.Candidates.Count == 1, caseId);
        Assert.Equal(expected, Oracle(result.Candidates[0]));
        Assert.Empty(result.Rejections);
    }

    [Theory]
    [InlineData("US-MBI-EMBED", "X1A23C45DE67Y", "BOUNDARY_VIOLATION", null)]
    [InlineData("US-MBI-EX-B", "MBI: 1B23C45DE67", "EXCLUDED_B", 2)]
    [InlineData("US-MBI-EX-I", "MBI: 1I23C45DE67", "EXCLUDED_I", 2)]
    [InlineData("US-MBI-EX-L", "MBI: 1L23C45DE67", "EXCLUDED_L", 2)]
    [InlineData("US-MBI-EX-O", "MBI: 1O23C45DE67", "EXCLUDED_O", 2)]
    [InlineData("US-MBI-EX-S", "MBI: 1S23C45DE67", "EXCLUDED_S", 2)]
    [InlineData("US-MBI-EX-Z", "MBI: 1Z23C45DE67", "EXCLUDED_Z", 2)]
    [InlineData("US-MBI-N01", "MBI: 1B23C45DE67", "EXCLUDED_B", 2)] // MBI_EXCLUDED_B_AT_POSITION_2
    [InlineData("US-MBI-FIRST0", "MBI: 0A23C45DE67", "POSITION1_REQUIRES_1_9", 1)]
    [InlineData("US-MBI-LEN10", "MBI: 1A23C45DE6", "WRONG_LENGTH", null)]
    [InlineData("US-MBI-NEG-BADGROUP", "MBI: 1A2-3C45-DE67", "GROUPING_MUST_BE_4_3_4", null)]
    [InlineData("US-MBI-NEG-MIXSEP", "MBI: 1A23-C45 DE67", "GROUPING_SEPARATOR_MUST_BE_CONSISTENT", null)]
    [InlineData("US-MBI-NEG-POS2DIGIT", "MBI: 1123C45DE67", "POSITION2_REQUIRES_ALLOWED_LETTER", 2)]
    [InlineData("US-MBI-POS4", "MBI: 1A2AC45DE67", "POSITION4_REQUIRES_DIGIT", 4)]
    [InlineData("US-MBI-UNICODE", "MBI: １A23C45DE67", "NON_ASCII_LOOKALIKE", null)]
    public void FrozenMbiNegatives_ProduceNoCandidateAndTheFrozenReason(
        string caseId, string raw, string oracleReason, int? position)
    {
        var result = UsMbiScanner.Detect(raw);

        Assert.True(result.Candidates.Count == 0, caseId);
        var match = Assert.Single(result.Rejections, r => OracleReason(r) == oracleReason);
        Assert.Equal(position, match.Position);
    }

    // ---- SEVIS ----------------------------------------------------------------------------------------------------

    [Fact]
    public void FrozenSevisPositive_US_SEVIS_P01_UnlabeledExactShapeIsLevelThreeStructureOnly()
    {
        var result = UsSevisScanner.Detect("N1234567890");

        Assert.Equal("us.sevis|UsSevis|L3|S|0:11|N1234567890", Oracle(Assert.Single(result.Candidates)));
        Assert.Empty(result.Rejections);
    }

    [Theory]
    [InlineData("US-SEVIS-NEG-EMBED", "XN1234567890Y", "ALPHANUMERIC_BOUNDARY_VIOLATION")]
    [InlineData("US-SEVIS-NEG-LEN11", "N12345678901", "WRONG_LENGTH_10_DIGITS_REQUIRED")]
    [InlineData("US-SEVIS-NEG-LEN9", "N123456789", "WRONG_LENGTH_10_DIGITS_REQUIRED")]
    [InlineData("US-SEVIS-NEG-PREFIX", "M1234567890", "PREFIX_MUST_BE_N")]
    [InlineData("US-SEVIS-NEG-UNICODE", "N123456789０", "NON_ASCII_DIGIT")]
    public void FrozenSevisNegatives_ProduceNoCandidateAndTheFrozenReason(string caseId, string raw, string oracleReason)
    {
        var result = UsSevisScanner.Detect(raw);

        Assert.True(result.Candidates.Count == 0, caseId);
        Assert.Contains(result.Rejections, r => OracleReason(r) == oracleReason);
    }

    // ---- scope ----------------------------------------------------------------------------------------------------

    [Fact]
    public void FrozenScopeNonGoal_US_SCOPE_FN_MEDREC_BroadMedicalRecordIdsAreOutForBothDetectors()
    {
        const string raw = "Medical Record Number: MR12345";

        Assert.Empty(UsMbiScanner.Detect(raw).Candidates);
        Assert.Empty(UsSevisScanner.Detect(raw).Candidates);
    }
}
