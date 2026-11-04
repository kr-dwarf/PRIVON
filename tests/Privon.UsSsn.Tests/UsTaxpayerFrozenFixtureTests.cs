using Privon.Detection;
using Privon.UsSsn;

namespace Privon.UsSsn.Tests;

// SYNTHETIC DATA ONLY. Raw inputs and expected outcomes replicate the FROZEN D3A corpus (read-only; not modified).
// EXPERIMENT ONLY: not an official detector, not registered, not a security determination.
public class UsTaxpayerFrozenFixtureTests
{
    private static string Oracle(UsTaxpayerCandidate c)
    {
        string kind = c.Kind switch { UsTaxpayerKind.Atin => "atin|UsAtin", UsTaxpayerKind.Itin => "itin|UsItin", _ => "ssn|UsSsn" };
        string evidence = c.Evidence == (UsSsnEvidence.Structure | UsSsnEvidence.Context) ? "S+C"
            : c.Evidence == UsSsnEvidence.Structure ? "S" : "?";
        return $"us.{kind}|L3|{evidence}|{c.Span.Start}:{c.Span.Length}|{c.Canonical.GetDigitsForTestingOnly()}";
    }

    private static string OracleReason(UsTaxpayerRejectionReason reason) => reason switch
    {
        UsTaxpayerRejectionReason.Area000 => "SSN_AREA_000_INVALID",
        UsTaxpayerRejectionReason.Area666 => "SSN_AREA_666_INVALID",
        UsTaxpayerRejectionReason.Area900To999 => "SSN_AREA_900_999_INVALID",
        UsTaxpayerRejectionReason.Group00 => "SSN_GROUP_00_INVALID",
        UsTaxpayerRejectionReason.Serial0000 => "SSN_SERIAL_0000_INVALID",
        UsTaxpayerRejectionReason.ItinRangeBelow50 => "ITIN_RANGE_BELOW_50",
        UsTaxpayerRejectionReason.ItinRangeGap66To69 => "ITIN_RANGE_GAP_66_69",
        UsTaxpayerRejectionReason.ItinRange89Excluded => "ITIN_RANGE_89_EXCLUDED",
        UsTaxpayerRejectionReason.ItinRange93Excluded => "ITIN_RANGE_93_EXCLUDED",
        UsTaxpayerRejectionReason.AtinRequires93 => "ATIN_REQUIRES_93",
        UsTaxpayerRejectionReason.UnsupportedSeparator => "UNSUPPORTED_SEPARATOR",
        UsTaxpayerRejectionReason.NonAsciiDigit => "NON_ASCII_DIGIT",
        UsTaxpayerRejectionReason.AlphanumericBoundaryViolation => "ALPHANUMERIC_BOUNDARY_VIOLATION",
        UsTaxpayerRejectionReason.InvalidLength => "INVALID_LENGTH",
        UsTaxpayerRejectionReason.NextRecognizedFieldTerminatesBinding => "NEXT_RECOGNIZED_FIELD_TERMINATES_BINDING",
        _ => "NOT_A_FROZEN_ORACLE_REASON",
    };

    private static IEnumerable<UsTaxpayerRejectionReason> Reasons(UsTaxpayerFamilyResult r, UsTaxpayerKind kind) =>
        r.Attempts.Where(a => a.Kind == kind && a.Outcome == UsTaxpayerAttemptOutcome.Rejected).Select(a => a.Reason!.Value);

    // ---- positives: every emitted candidate is exactly the frozen one, and it is the ONLY candidate ----

    [Theory]
    [InlineData("US-ATIN-COMPACT-LBL", "ATIN: 912934567", "us.atin|UsAtin|L3|S+C|6:9|912934567")]
    [InlineData("US-ATIN-SPACED-LBL", "ATIN: 912 93 4567", "us.atin|UsAtin|L3|S+C|6:11|912934567")]
    [InlineData("US-ITIN-COMPACT-LBL", "ITIN: 912701234", "us.itin|UsItin|L3|S+C|6:9|912701234")]
    [InlineData("US-ITIN-SPACED-LBL", "ITIN: 912 70 1234", "us.itin|UsItin|L3|S+C|6:11|912701234")]
    [InlineData("US-ITIN-P01", "ITIN: 912-70-1234", "us.itin|UsItin|L3|S+C|6:11|912701234")]
    [InlineData("US-ITIN-R01", "ITIN: 912-50-1234", "us.itin|UsItin|L3|S+C|6:11|912501234")]
    [InlineData("US-ITIN-R02", "ITIN: 912-65-1234", "us.itin|UsItin|L3|S+C|6:11|912651234")]
    [InlineData("US-ITIN-R03", "ITIN: 912-70-1234", "us.itin|UsItin|L3|S+C|6:11|912701234")]
    [InlineData("US-ITIN-R04", "ITIN: 912-88-1234", "us.itin|UsItin|L3|S+C|6:11|912881234")]
    [InlineData("US-ITIN-R05", "ITIN: 912-90-1234", "us.itin|UsItin|L3|S+C|6:11|912901234")]
    [InlineData("US-ITIN-R06", "ITIN: 912-92-1234", "us.itin|UsItin|L3|S+C|6:11|912921234")]
    [InlineData("US-ITIN-R07", "ITIN: 912-94-1234", "us.itin|UsItin|L3|S+C|6:11|912941234")]
    [InlineData("US-ITIN-R08", "ITIN: 912-99-1234", "us.itin|UsItin|L3|S+C|6:11|912991234")]
    [InlineData("US-TAX-MX01", "ATIN: 912-93-4567", "us.atin|UsAtin|L3|S+C|6:11|912934567")]
    [InlineData("US-TAX-MX02", "912-93-4567", "us.atin|UsAtin|L3|S|0:11|912934567")]
    [InlineData("US-SSN-P01", "SSN: 321-54-9876", "us.ssn|UsSsn|L3|S+C|5:11|321549876")]
    [InlineData("US-SSN-X01", "321-54-9876", "us.ssn|UsSsn|L3|S|0:11|321549876")]
    [InlineData("US-SSN-X02", "SSN: 321549876", "us.ssn|UsSsn|L3|S+C|5:9|321549876")]
    [InlineData("US-SSN-X03", "Social Security Number: 321 54 9876", "us.ssn|UsSsn|L3|S+C|24:11|321549876")]
    public void FrozenPositiveFixtures_EmitExactlyOneFrozenCandidate(string caseId, string raw, string expected)
    {
        var result = UsTaxpayerFamily.Evaluate(raw);

        Assert.True(result.Candidates.Count == 1, caseId);
        Assert.Equal(expected, Oracle(result.Candidates[0]));
    }

    [Fact]
    public void FrozenMixedFixtureMX01_SuppressedAttemptsAreTheFrozenOnes()
    {
        var result = UsTaxpayerFamily.Evaluate("ATIN: 912-93-4567");

        Assert.Equal(["ITIN_RANGE_93_EXCLUDED"], Reasons(result, UsTaxpayerKind.Itin).Select(OracleReason));
        Assert.Equal(["SSN_AREA_900_999_INVALID"], Reasons(result, UsTaxpayerKind.Ssn).Select(OracleReason));
        Assert.Equal(new RawSpan(6, 11), Assert.Single(result.Candidates).Span);
    }

    [Fact]
    public void FrozenResolverFixtureMX02_ThreeAttempts_OneEmission_NoRejectedAttemptEmitted()
    {
        var result = UsTaxpayerFamily.Evaluate("912-93-4567");

        var accepted = Assert.Single(result.Attempts, a => a.Outcome == UsTaxpayerAttemptOutcome.Accepted);
        Assert.Equal(UsTaxpayerKind.Atin, accepted.Kind);
        Assert.Equal(3, result.Attempts.Count);
        Assert.Equal(["ITIN_RANGE_93_EXCLUDED"], Reasons(result, UsTaxpayerKind.Itin).Select(OracleReason));
        Assert.Equal(["SSN_AREA_900_999_INVALID"], Reasons(result, UsTaxpayerKind.Ssn).Select(OracleReason));
        Assert.Equal(UsTaxpayerKind.Atin, Assert.Single(result.Candidates).Kind);
        Assert.DoesNotContain(result.Candidates, c => c.Kind != UsTaxpayerKind.Atin);
    }

    // ---- unlabeled compact numbers are OUT for every type ----

    [Theory]
    [InlineData("US-ATIN-FN01", "912934567")]
    [InlineData("US-ITIN-FN01", "912701234")]
    [InlineData("US-SSN-FN01", "321549876")]
    public void FrozenFalseNegativeFixtures_UnlabeledCompactIsNeverATaxpayerNumber(string caseId, string raw)
    {
        Assert.True(UsTaxpayerFamily.Evaluate(raw).Candidates.Count == 0, caseId);
    }

    // ---- negatives: no candidate, with the frozen reason attributed to the target detector ----

    [Theory]
    [InlineData("US-ITIN-RN01", "ITIN: 912-49-1234", UsTaxpayerKind.Itin, UsTaxpayerRejectionReason.ItinRangeBelow50)]
    [InlineData("US-ITIN-RN02", "ITIN: 912-66-1234", UsTaxpayerKind.Itin, UsTaxpayerRejectionReason.ItinRangeGap66To69)]
    [InlineData("US-ITIN-RN03", "ITIN: 912-69-1234", UsTaxpayerKind.Itin, UsTaxpayerRejectionReason.ItinRangeGap66To69)]
    [InlineData("US-ITIN-RN04", "ITIN: 912-89-1234", UsTaxpayerKind.Itin, UsTaxpayerRejectionReason.ItinRange89Excluded)]
    [InlineData("US-ITIN-RN05", "ITIN: 912-93-1234", UsTaxpayerKind.Itin, UsTaxpayerRejectionReason.ItinRange93Excluded)]
    [InlineData("US-ATIN-N92", "ATIN: 912-92-1234", UsTaxpayerKind.Atin, UsTaxpayerRejectionReason.AtinRequires93)]
    [InlineData("US-ATIN-N94", "ATIN: 912-94-1234", UsTaxpayerKind.Atin, UsTaxpayerRejectionReason.AtinRequires93)]
    [InlineData("US-ITIN-NEG-DOT", "ITIN: 912.70.1234", UsTaxpayerKind.Itin, UsTaxpayerRejectionReason.UnsupportedSeparator)]
    [InlineData("US-ATIN-NEG-DOT", "ATIN: 912.93.1234", UsTaxpayerKind.Atin, UsTaxpayerRejectionReason.UnsupportedSeparator)]
    [InlineData("US-ITIN-NEG-EMBED", "X912-70-1234Y", UsTaxpayerKind.Itin, UsTaxpayerRejectionReason.AlphanumericBoundaryViolation)]
    [InlineData("US-ATIN-NEG-EMBED", "X912-93-1234Y", UsTaxpayerKind.Atin, UsTaxpayerRejectionReason.AlphanumericBoundaryViolation)]
    [InlineData("US-ITIN-NEG-UNICODE", "ITIN: 912-70-123４", UsTaxpayerKind.Itin, UsTaxpayerRejectionReason.NonAsciiDigit)]
    [InlineData("US-ATIN-NEG-UNICODE", "ATIN: 912-93-123４", UsTaxpayerKind.Atin, UsTaxpayerRejectionReason.NonAsciiDigit)]
    [InlineData("US-ATIN-NEG-LEN", "ATIN: 912-93-123", UsTaxpayerKind.Atin, UsTaxpayerRejectionReason.InvalidLength)]
    public void FrozenNegativeFixtures_NoCandidateAndTheFrozenReasonForTheTargetType(
        string caseId, string raw, UsTaxpayerKind target, UsTaxpayerRejectionReason reason)
    {
        var result = UsTaxpayerFamily.Evaluate(raw);

        // The frozen negatives run ONLY the target detector (TargetDetectorOnly). The family also evaluates the other types, so
        // a number valid for another type (92/94 are ITIN groups, 93 is the ATIN group) is correctly emitted as THAT type.
        Assert.DoesNotContain(result.Candidates, c => c.Kind == target);
        Assert.True(result.Candidates.Count <= 1, caseId);
        Assert.Contains(reason, Reasons(result, target));
        Assert.NotEqual("NOT_A_FROZEN_ORACLE_REASON", OracleReason(reason));
    }

    [Fact]
    public void FrozenNegativeFixture_ITIN_NEG_NEXTLABEL_UsesTheCallerSuppliedLabelSet()
    {
        // The branch "an unlabeled-shaped number inside a field whose binding was ended by a following recognized
        // label" is only pinned for THIS frozen case; its general policy is unselected (see UsTaxpayerFamily docs).
        var result = UsTaxpayerFamily.Evaluate("ITIN: Phone: 912-70-1234", UsTaxpayerLabelSet.Default.WithOthers("Phone"));

        Assert.Empty(result.Candidates);
        Assert.Contains(UsTaxpayerRejectionReason.NextRecognizedFieldTerminatesBinding, Reasons(result, UsTaxpayerKind.Itin));
        Assert.Equal("NEXT_RECOGNIZED_FIELD_TERMINATES_BINDING", OracleReason(UsTaxpayerRejectionReason.NextRecognizedFieldTerminatesBinding));
    }
}
