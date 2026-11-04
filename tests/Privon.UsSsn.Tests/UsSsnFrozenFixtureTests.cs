using Privon.Detection;
using Privon.UsSsn;

namespace Privon.UsSsn.Tests;

// SYNTHETIC DATA ONLY. These cases replicate, verbatim, the SSN-relevant raw inputs and expected outcomes of the
// FROZEN D3A corpus (read-only; the corpus files are not modified). CaseIds are cited for traceability only.
// EXPERIMENT ONLY: this module is not an official detector, is not registered, and is not a security determination.
public class UsSsnFrozenFixtureTests
{
    // Mirror of the frozen oracle's candidate record, minus the alias token/rank, which belong to the pipeline.
    private static string Oracle(UsSsnCandidate c) =>
        $"us.ssn|UsSsn|L3|{(c.Evidence == (UsSsnEvidence.Structure | UsSsnEvidence.Context) ? "S+C" : c.Evidence == UsSsnEvidence.Structure ? "S" : "?")}|{c.Span.Start}:{c.Span.Length}|{c.Canonical.GetDigitsForTestingOnly()}";

    [Theory]
    [InlineData("US-SSN-P01", "SSN: 321-54-9876", "us.ssn|UsSsn|L3|S+C|5:11|321549876")]
    [InlineData("US-SSN-X01", "321-54-9876", "us.ssn|UsSsn|L3|S|0:11|321549876")]
    [InlineData("US-SSN-X02", "SSN: 321549876", "us.ssn|UsSsn|L3|S+C|5:9|321549876")]
    [InlineData("US-SSN-X03", "Social Security Number: 321 54 9876", "us.ssn|UsSsn|L3|S+C|24:11|321549876")]
    [InlineData("FLD-END-CR", "SSN: 321549876\rmemo", "us.ssn|UsSsn|L3|S+C|5:9|321549876")]
    [InlineData("FLD-END-LF", "SSN: 321549876\nmemo", "us.ssn|UsSsn|L3|S+C|5:9|321549876")]
    [InlineData("FLD-END-PIPE", "SSN: 321549876|memo", "us.ssn|UsSsn|L3|S+C|5:9|321549876")]
    [InlineData("FLD-END-SEMI", "SSN: 321549876;memo", "us.ssn|UsSsn|L3|S+C|5:9|321549876")]
    [InlineData("FLD-END-TAB", "SSN: 321549876\tmemo", "us.ssn|UsSsn|L3|S+C|5:9|321549876")]
    public void FrozenPositiveFixtures_ProduceExactlyTheFrozenCandidate(string caseId, string raw, string expected)
    {
        var result = UsSsnScanner.Detect(raw);

        Assert.True(result.Candidates.Count == 1, caseId);
        Assert.Equal(expected, Oracle(result.Candidates[0]));
        Assert.Empty(result.Rejections);
    }

    [Fact]
    public void FrozenNextLabelFixture_FLD_END_NEXTLABEL_UsesTheCallerSuppliedLabelSet()
    {
        const string raw = "SSN: 321549876 Phone: 2125551212";

        var withPhoneLabel = UsSsnScanner.Detect(raw, UsSsnScanner.DefaultStrongLabels, ["Phone"]);
        var withoutPhoneLabel = UsSsnScanner.Detect(raw);

        Assert.Equal("us.ssn|UsSsn|L3|S+C|5:9|321549876", Oracle(Assert.Single(withPhoneLabel.Candidates)));
        Assert.Empty(withoutPhoneLabel.Candidates); // the label set is an input; without it the field runs on
    }

    [Fact]
    public void FrozenNegativeFixture_FN01_UnlabeledCompactNineDigitsIsOut()
    {
        var result = UsSsnScanner.Detect("321549876");

        Assert.Empty(result.Candidates);
    }

    [Theory]
    [InlineData("US-SSN-N01", "SSN: 000-54-9876", UsSsnRejectionReason.Area000, "SSN_AREA_000_INVALID")]
    [InlineData("US-SSN-XN01", "666-54-9876", UsSsnRejectionReason.Area666, "SSN_AREA_666_INVALID")]
    [InlineData("US-SSN-XN02", "900-54-9876", UsSsnRejectionReason.Area900To999, "SSN_AREA_900_999_INVALID")]
    [InlineData("US-SSN-XN03", "321-00-9876", UsSsnRejectionReason.Group00, "SSN_GROUP_00_INVALID")]
    [InlineData("US-SSN-XN04", "321-54-0000", UsSsnRejectionReason.Serial0000, "SSN_SERIAL_0000_INVALID")]
    [InlineData("US-SSN-XN05", "SSN: 321.54.9876", UsSsnRejectionReason.UnsupportedSeparator, "UNSUPPORTED_SEPARATOR")]
    [InlineData("US-SSN-XN06", "SSN: 321-54-987６", UsSsnRejectionReason.NonAsciiDigit, "NON_ASCII_DIGIT")]
    [InlineData("US-SSN-XN07", "X321-54-9876Y", UsSsnRejectionReason.AlphanumericBoundaryViolation, "ALPHANUMERIC_BOUNDARY_VIOLATION")]
    public void FrozenNegativeFixtures_ProduceNoCandidateAndTheFrozenReason(
        string caseId, string raw, UsSsnRejectionReason reason, string oracleReason)
    {
        var result = UsSsnScanner.Detect(raw);

        Assert.True(result.Candidates.Count == 0, caseId);
        Assert.Contains(result.Rejections, r => r.Reason == reason);
        Assert.Equal(oracleReason, OracleReasonCode(reason));
    }

    private static string OracleReasonCode(UsSsnRejectionReason reason) => reason switch
    {
        UsSsnRejectionReason.Area000 => "SSN_AREA_000_INVALID",
        UsSsnRejectionReason.Area666 => "SSN_AREA_666_INVALID",
        UsSsnRejectionReason.Area900To999 => "SSN_AREA_900_999_INVALID",
        UsSsnRejectionReason.Group00 => "SSN_GROUP_00_INVALID",
        UsSsnRejectionReason.Serial0000 => "SSN_SERIAL_0000_INVALID",
        UsSsnRejectionReason.UnsupportedSeparator => "UNSUPPORTED_SEPARATOR",
        UsSsnRejectionReason.NonAsciiDigit => "NON_ASCII_DIGIT",
        UsSsnRejectionReason.AlphanumericBoundaryViolation => "ALPHANUMERIC_BOUNDARY_VIOLATION",
        _ => "NOT_A_FROZEN_ORACLE_REASON",
    };
}
