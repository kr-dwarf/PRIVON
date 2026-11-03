using Privon.Core;
using Privon.Detection;
using Privon.UsProductIntegration;
using static Privon.UsProductIntegration.Tests.UsProductHarness;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY. Conversion of the isolated cores' results into REAL DetectionCandidate values. The expected
// records are the FROZEN D3A oracle records (read-only; copied verbatim), not values derived from the implementation.
public class UsProductConversionTests
{
    // caseId, detector index in AllFive() (0 atin,1 itin,2 ssn,3 mbi,4 sevis), raw, frozen oracle record
    public static TheoryData<string, int, string, string> FrozenPositives => new()
    {
        { "US-SSN-P01", 2, "SSN: 321-54-9876", "us.ssn|UsSsn|L3|S+C|5:11|321549876|[SSN1]|70" },
        { "US-SSN-X01", 2, "321-54-9876", "us.ssn|UsSsn|L3|S|0:11|321549876|[SSN1]|70" },
        { "US-SSN-X02", 2, "SSN: 321549876", "us.ssn|UsSsn|L3|S+C|5:9|321549876|[SSN1]|70" },
        { "US-SSN-X03", 2, "Social Security Number: 321 54 9876", "us.ssn|UsSsn|L3|S+C|24:11|321549876|[SSN1]|70" },
        { "US-ITIN-P01", 1, "ITIN: 912-70-1234", "us.itin|UsItin|L3|S+C|6:11|912701234|[ITIN1]|60" },
        { "US-ITIN-COMPACT-LBL", 1, "ITIN: 912701234", "us.itin|UsItin|L3|S+C|6:9|912701234|[ITIN1]|60" },
        { "US-ITIN-SPACED-LBL", 1, "ITIN: 912 70 1234", "us.itin|UsItin|L3|S+C|6:11|912701234|[ITIN1]|60" },
        { "US-ITIN-R01", 1, "ITIN: 912-50-1234", "us.itin|UsItin|L3|S+C|6:11|912501234|[ITIN1]|60" },
        { "US-ITIN-R02", 1, "ITIN: 912-65-1234", "us.itin|UsItin|L3|S+C|6:11|912651234|[ITIN1]|60" },
        { "US-ITIN-R04", 1, "ITIN: 912-88-1234", "us.itin|UsItin|L3|S+C|6:11|912881234|[ITIN1]|60" },
        { "US-ITIN-R05", 1, "ITIN: 912-90-1234", "us.itin|UsItin|L3|S+C|6:11|912901234|[ITIN1]|60" },
        { "US-ITIN-R06", 1, "ITIN: 912-92-1234", "us.itin|UsItin|L3|S+C|6:11|912921234|[ITIN1]|60" },
        { "US-ITIN-R07", 1, "ITIN: 912-94-1234", "us.itin|UsItin|L3|S+C|6:11|912941234|[ITIN1]|60" },
        { "US-ITIN-R08", 1, "ITIN: 912-99-1234", "us.itin|UsItin|L3|S+C|6:11|912991234|[ITIN1]|60" },
        { "US-ATIN-COMPACT-LBL", 0, "ATIN: 912934567", "us.atin|UsAtin|L3|S+C|6:9|912934567|[ATIN1]|50" },
        { "US-ATIN-SPACED-LBL", 0, "ATIN: 912 93 4567", "us.atin|UsAtin|L3|S+C|6:11|912934567|[ATIN1]|50" },
        { "US-TAX-MX01", 0, "ATIN: 912-93-4567", "us.atin|UsAtin|L3|S+C|6:11|912934567|[ATIN1]|50" },
        { "US-TAX-MX02", 0, "912-93-4567", "us.atin|UsAtin|L3|S|0:11|912934567|[ATIN1]|50" },
        { "US-MBI-P01", 3, "MBI: 1A23C45DE67", "us.mbi|UsMbi|L3|S+C|5:11|1A23C45DE67|[MBI1]|80" },
        { "US-MBI-G01", 3, "MBI: 1A23-C45-DE67", "us.mbi|UsMbi|L3|S+C|5:13|1A23C45DE67|[MBI1]|80" },
        { "US-MBI-G02", 3, "MBI: 1A23 C45 DE67", "us.mbi|UsMbi|L3|S+C|5:13|1A23C45DE67|[MBI1]|80" },
        { "US-MBI-LOWER", 3, "MBI: 1a23c45de67", "us.mbi|UsMbi|L3|S+C|5:11|1A23C45DE67|[MBI1]|80" },
        { "US-SEVIS-P01", 4, "N1234567890", "us.sevis|UsSevis|L3|S|0:11|N1234567890|[SEVIS1]|90" },
    };

    [Theory]
    [MemberData(nameof(FrozenPositives))]
    public void FrozenPositives_ProduceExactlyTheFrozenCandidateRecord_AndNoOtherDetectorClaimsTheValue(
        string caseId, int detectorIndex, string raw, string expected)
    {
        var detectors = AllFive();

        var own = Run(detectors[detectorIndex], raw);

        Assert.True(own.Count == 1, caseId);
        Assert.Equal(expected, Oracle(own[0]));
        for (int i = 0; i < detectors.Length; i++)
        {
            if (i != detectorIndex)
            {
                Assert.Empty(Run(detectors[i], raw)); // TargetDetectorOnly: each detector reports only its own type
            }
        }
    }

    // The frozen negatives are TargetDetectorOnly fixtures: the named target detector must report nothing. Another
    // type may legitimately claim the same text (900-54-9876 is not an SSN, but it is a structurally valid ITIN).
    [Theory]
    [InlineData("US-SSN-XN01", 2, "666-54-9876")]
    [InlineData("US-SSN-XN02", 2, "900-54-9876")]
    [InlineData("US-SSN-XN03", 2, "321-00-9876")]
    [InlineData("US-SSN-XN04", 2, "321-54-0000")]
    [InlineData("US-SSN-XN05", 2, "SSN: 321.54.9876")]
    [InlineData("US-SSN-XN06", 2, "SSN: 321-54-987\uFF16")]
    [InlineData("US-SSN-XN07", 2, "X321-54-9876Y")]
    [InlineData("US-MBI-EX-B", 3, "MBI: 1B23C45DE67")]
    [InlineData("US-MBI-EMBED", 3, "X1A23C45DE67Y")]
    [InlineData("US-MBI-UNICODE", 3, "MBI: \uFF11A23C45DE67")]
    [InlineData("US-SEVIS-NEG-LEN9", 4, "N123456789")]
    [InlineData("US-SEVIS-NEG-EMBED", 4, "XN1234567890Y")]
    [InlineData("FN01", 2, "321549876")]
    public void FrozenNegatives_ProduceNoCandidateFromTheTargetDetector(string caseId, int targetIndex, string raw)
    {
        Assert.True(Run(AllFive()[targetIndex], raw).Count == 0, caseId);
    }

    [Fact]
    public void ASingleRawValue_CanBeAnItinWhileNotBeingAnSsn_ThisIsWhyTheNegativesAreTargetSpecific()
    {
        Assert.Empty(Run(new UsSsnCandidateDetector(), "900-54-9876"));
        Assert.Equal("us.itin", Assert.Single(Run(new UsItinCandidateDetector(), "900-54-9876")).DetectorId.Value);
    }

    [Fact]
    public void EveryCandidate_CarriesTheContractFieldsAndNothingElse()
    {
        const string raw = "ATIN: 912-93-4567 ITIN: 912-70-1234 SSN: 321-54-9876 MBI: 1A23C45DE67 SEVIS: N1234567890";
        var labels = new[] { "Phone" };
        _ = labels;

        var all = RunAll(raw);

        Assert.Equal(5, all.Count);
        foreach (var c in all)
        {
            Assert.Equal(RiskLevel.Level3, c.RiskLevel);                  // every U.S. identifier here is Level 3
            Assert.Equal(DetectionConfidence.High, c.Confidence);
            Assert.True(c.DetectorId.HasValue);
            Assert.NotEqual(DetectionCandidate.UnassignedRank, c.ResolutionRank); // the identity was stamped, not defaulted
            Assert.NotEqual(ResolutionEvidence.NotModelled, c.Evidence);
            Assert.Null(c.AliasIdentity);                                 // aliases are assigned downstream, as for every legacy detector
            Assert.Equal(OwnerClaim.NotApplicable, c.OwnerClaim);         // no field-ownership claim is invented here
            Assert.Null(c.AdditionalNormativeFields);                     // R0.5 forbids semantic metadata outside the frozen fields
            Assert.NotNull(c.Canonical);
            Assert.Equal(c.PiiType, c.Canonical!.Value.PiiType);
            Assert.EndsWith("CandidateDetector", c.DetectorName);
        }
    }

    [Theory]
    [InlineData("ＡＢ ﬁ \U0001F600 SSN: 321-54-9876", 2, "321-54-9876")]
    [InlineData("①② ITIN: 912-70-1234", 1, "912-70-1234")]
    [InlineData("éé \U0001F600 ATIN: 912 93 4567", 0, "912 93 4567")]
    [InlineData("Ａ MBI: 1a23 c45 de67", 3, "1a23 c45 de67")]
    [InlineData("\U0001F600 \U0001F600 SEVIS ID: N1234567890", 4, "N1234567890")]
    public void TheSpanIsTheExactRawSpan_EvenWhenNormalizationChangesTheTextBeforeIt(
        string raw, int detectorIndex, string expectedSlice)
    {
        var candidate = Assert.Single(Run(AllFive()[detectorIndex], raw));

        Assert.Equal(expectedSlice, Slice(raw, candidate));
    }

    [Fact]
    public void SeveralOccurrences_AreSeveralCandidates_InTextOrder_WithExactSpans()
    {
        const string raw = "a 321-54-9876, b 321-54-9876; c 123-45-6789";

        var found = Run(new UsSsnCandidateDetector(), raw);

        Assert.Equal(["2:11", "17:11", "32:11"], found.Select(c => $"{c.Span.Start}:{c.Span.Length}"));
        Assert.Equal(["321549876", "321549876", "123456789"], found.Select(c => c.Canonical!.Value.Value));
    }

    [Fact]
    public void ANumberInsideAnotherFieldsValue_IsNotATaxpayerCandidate_TheModulesExistingOwnershipBehaviourIsPreserved()
    {
        // Same assumptions as the module's own tests: a recognized label owns its value region.
        var withPhone = new UsSsnCandidateDetector(
            Privon.UsSsn.UsTaxpayerLabelSet.Default.WithOthers("Phone"));

        var found = Run(withPhone, "SSN: 321-54-9876 Phone: 321-54-9877");

        Assert.Equal("5:11", $"{found.Single().Span.Start}:{found.Single().Span.Length}");
    }

    // ---- invalid input and failure states ----------------------------------------------------------------------

    [Fact]
    public void ANullContext_IsAnArgumentException_NeverAnEmptyResult()
    {
        foreach (var detector in AllFive())
        {
            Assert.Throws<ArgumentNullException>(() => detector.Detect(null!));
        }
    }

    [Fact]
    public void NullConstructorArguments_AreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new UsSsnCandidateDetector(null!));
        Assert.Throws<ArgumentNullException>(() => new UsItinCandidateDetector(null!));
        Assert.Throws<ArgumentNullException>(() => new UsAtinCandidateDetector(null!));
        Assert.Throws<ArgumentNullException>(() => new UsMbiCandidateDetector(null!));
        Assert.Throws<ArgumentNullException>(() => new UsSevisCandidateDetector(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no identifiers in this text")]
    public void TextWithNothingToFind_IsAGenuineEmptyResult(string raw)
    {
        Assert.Empty(RunAll(raw));
    }

    [Fact]
    public void AnInternalFailureOfTheCore_IsNotSwallowedIntoAnEmptyList()
    {
        // A label set holding a null entry makes the core throw; the adapter must let that surface as a failure.
        var broken = new Privon.UsSsn.UsTaxpayerLabelSet(null!, [], [], []);
        var detector = new UsSsnCandidateDetector(broken);

        Assert.ThrowsAny<Exception>(() => Run(detector, "SSN: 321-54-9876"));
    }

    [Fact]
    public void TheDetectors_AreDeterministic_AndHoldNoStateBetweenCalls()
    {
        const string raw = "ATIN: 912-93-4567 MBI: 1A23C45DE67 SEVIS: N1234567890 SSN: 321-54-9876";

        var first = RunAll(raw).Select(Oracle).ToList();
        var second = RunAll(raw).Select(Oracle).ToList();
        var detector = new UsMbiCandidateDetector();
        var a = Run(detector, "MBI: 1A23C45DE67");
        _ = Run(detector, "no identifiers here");
        var b = Run(detector, "MBI: 1A23C45DE67");

        Assert.Equal(first, second);
        Assert.Equal(a.Select(Oracle), b.Select(Oracle));
    }

    [Fact]
    public void ResultLists_AreNotMutableViewsOntoDetectorState()
    {
        var detector = new UsSsnCandidateDetector();

        var first = Run(detector, "SSN: 321-54-9876");
        var second = Run(detector, "SSN: 321-54-9876");

        Assert.NotSame(first, second);
        Assert.Single(first);
        Assert.Single(second);
    }
}
