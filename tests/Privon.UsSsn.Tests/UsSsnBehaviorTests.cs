using Privon.Detection;
using Privon.UsSsn;

namespace Privon.UsSsn.Tests;

// SYNTHETIC DATA ONLY: every number below is an invented test value, never a real person's identifier.
// EXPERIMENT ONLY. Cases marked [EA] pin an EXPERIMENT ASSUMPTION for a branch the frozen corpus does not
// decide; they must not be read as the official rule.
public class UsSsnBehaviorTests
{
    private static string Digits(UsSsnCandidate c) => c.Canonical.GetDigitsForTestingOnly();

    // ---- 1/2. allowed formats and strong labels ----

    [Theory]
    [InlineData("SSN: 321-54-9876", 5, 11, UsSsnForm.Dashed)]
    [InlineData("SSN: 321549876", 5, 9, UsSsnForm.Compact)]
    [InlineData("SSN: 321 54 9876", 5, 11, UsSsnForm.Spaced)]
    [InlineData("ssn: 321549876", 5, 9, UsSsnForm.Compact)]
    [InlineData("Ssn=321549876", 4, 9, UsSsnForm.Compact)]
    [InlineData("SSN #321-54-9876", 5, 11, UsSsnForm.Dashed)]
    [InlineData("SSN 321549876", 4, 9, UsSsnForm.Compact)]
    [InlineData("social security number: 321549876", 24, 9, UsSsnForm.Compact)]
    [InlineData("Id list\nSSN: 321-54-9876\nend", 13, 11, UsSsnForm.Dashed)]
    public void StrongLabelledValidNumbers_YieldOneContextCandidate(string raw, int start, int length, UsSsnForm form)
    {
        var candidate = Assert.Single(UsSsnScanner.Detect(raw).Candidates);

        Assert.Equal(new RawSpan(start, length), candidate.Span);
        Assert.Equal(UsSsnEvidence.Structure | UsSsnEvidence.Context, candidate.Evidence);
        Assert.Equal(form, candidate.Form);
        Assert.Equal("321549876", Digits(candidate));
    }

    [Fact]
    public void CandidateSpan_IsTheExactRawSpanOfTheNumber()
    {
        const string raw = "ref ##  SSN:  321-54-9876 ;tail";

        var candidate = Assert.Single(UsSsnScanner.Detect(raw).Candidates);

        Assert.Equal("321-54-9876", raw.Substring(candidate.Span.Start, candidate.Span.Length));
    }

    [Fact]
    public void UnlabeledDashedNumber_IsAStructureOnlyCandidate_AndLabelledOneIsNotDuplicated()
    {
        var plain = Assert.Single(UsSsnScanner.Detect("call 321-54-9876 now").Candidates);
        var labelled = UsSsnScanner.Detect("SSN: 321-54-9876");

        Assert.Equal(UsSsnEvidence.Structure, plain.Evidence);
        Assert.Equal(5, plain.Span.Start);
        Assert.Single(labelled.Candidates); // label + structure merged into one S+C candidate, not two
    }

    // ---- 3. unlabeled nine digits are never an SSN ----

    [Theory]
    [InlineData("321549876")]
    [InlineData("order 321549876 shipped")]
    [InlineData("321549876\n")]
    [InlineData("id=321549876")]
    [InlineData("321 54 9876")] // [EA] unlabeled spaced form is not decided by the frozen corpus => no candidate
    public void UnlabeledCompactOrSpacedNumbers_AreNotCandidates(string raw)
    {
        var result = UsSsnScanner.Detect(raw);

        Assert.Empty(result.Candidates);
    }

    // ---- 4/5. ranges ----

    [Theory]
    [InlineData("SSN: 000-12-3456", UsSsnRejectionReason.Area000)]
    [InlineData("SSN: 666-12-3456", UsSsnRejectionReason.Area666)]
    [InlineData("SSN: 900-12-3456", UsSsnRejectionReason.Area900To999)]
    [InlineData("SSN: 950-12-3456", UsSsnRejectionReason.Area900To999)]
    [InlineData("SSN: 999-12-3456", UsSsnRejectionReason.Area900To999)]
    [InlineData("SSN: 123-00-3456", UsSsnRejectionReason.Group00)]
    [InlineData("SSN: 123-45-0000", UsSsnRejectionReason.Serial0000)]
    [InlineData("SSN: 000549876", UsSsnRejectionReason.Area000)]
    [InlineData("SSN: 666 54 9876", UsSsnRejectionReason.Area666)]
    public void OutOfRangeAreaGroupSerial_YieldNoCandidateAndTheSpecificReason(string raw, UsSsnRejectionReason reason)
    {
        var result = UsSsnScanner.Detect(raw);

        Assert.Empty(result.Candidates);
        Assert.Contains(result.Rejections, r => r.Reason == reason);
    }

    [Theory]
    [InlineData("SSN: 001-01-0001")]
    [InlineData("SSN: 665-01-0001")]
    [InlineData("SSN: 667-99-9999")]
    [InlineData("SSN: 899-99-9999")]
    public void RangeEdgesJustInsideTheValidRange_AreAccepted(string raw)
    {
        Assert.Single(UsSsnScanner.Detect(raw).Candidates);
    }

    [Fact]
    public void FirstFailingRuleWins_AreaBeforeGroupBeforeSerial() // [EA] ordering for multiple defects
    {
        Assert.Equal(UsSsnRejectionReason.Area000, Assert.Single(UsSsnScanner.Detect("SSN: 000-00-0000").Rejections).Reason);
        Assert.Equal(UsSsnRejectionReason.Group00, Assert.Single(UsSsnScanner.Detect("SSN: 123-00-0000").Rejections).Reason);
    }

    // ---- 6. SSN-like ordinary number strings ----

    [Theory]
    [InlineData("tel 555-12-3456789")]
    [InlineData("v1.2.3-45-6789")]
    [InlineData("2026-09-15")]
    [InlineData("12-34-5678")]
    [InlineData("1234-56-789")]
    [InlineData("321-5-49876")]
    [InlineData("321--54-9876")]
    [InlineData("SSN: 12-345-6789")]
    [InlineData("SSN: abc")]
    [InlineData("SSN:")]
    [InlineData("SSN")]
    public void OrdinaryNumberLikeStrings_YieldNoCandidate(string raw)
    {
        Assert.Empty(UsSsnScanner.Detect(raw).Candidates);
    }

    // ---- 7. adjacency / boundary errors ----

    [Theory]
    [InlineData("X321-54-9876Y")]
    [InlineData("a321-54-9876")]
    [InlineData("321-54-9876z")]
    [InlineData("1321-54-9876")]
    [InlineData("321-54-98761")]
    [InlineData("-321-54-9876")] // [EA] a hyphen touching the number is a boundary violation
    [InlineData("321-54-9876-1")] // [EA]
    [InlineData("é321-54-9876")] // non-ASCII letter touching
    [InlineData("SSN: 3215498761")] // ten digits
    [InlineData("SSN: x321549876")]
    [InlineData("SSN: 321549876x")]
    public void NumbersTouchingOtherCharacters_AreBoundaryViolations(string raw)
    {
        var result = UsSsnScanner.Detect(raw);

        Assert.Empty(result.Candidates);
        Assert.Contains(result.Rejections, r => r.Reason == UsSsnRejectionReason.AlphanumericBoundaryViolation);
    }

    [Fact]
    public void LabelsInsideLongerWords_AreNotStrongLabels() // [EA] ASCII alphanumeric boundary on the label itself
    {
        Assert.Empty(UsSsnScanner.Detect("CLASSN: 321549876").Candidates);
        Assert.Empty(UsSsnScanner.Detect("SSNX: 321549876").Candidates);
        Assert.Empty(UsSsnScanner.Detect("9SSN: 321549876").Candidates);
    }

    [Fact]
    public void TextAfterAnUnterminatedFieldValue_IsRejected_NotSilentlyAccepted() // [EA]
    {
        var result = UsSsnScanner.Detect("SSN: 321549876 thanks");

        Assert.Empty(result.Candidates);
        Assert.Contains(result.Rejections, r => r.Reason == UsSsnRejectionReason.TrailingContent);
    }

    // ---- 8. Unicode digits and mixed separators ----

    [Theory]
    [InlineData("SSN: 321-54-987６")] // fullwidth digit
    [InlineData("SSN: ３２１５４９８７６")] // fullwidth compact
    [InlineData("SSN: ٣٢١-٥٤-٩٨٧٦")] // Arabic-Indic digits
    [InlineData("321-54-987٦")] // unlabeled
    public void NonAsciiDigits_AreRejected_NotFoldedByNormalization(string raw)
    {
        var result = UsSsnScanner.Detect(raw);

        Assert.Empty(result.Candidates);
        Assert.Contains(result.Rejections, r => r.Reason == UsSsnRejectionReason.NonAsciiDigit);
    }

    [Theory]
    [InlineData("SSN: 321.54.9876", UsSsnRejectionReason.UnsupportedSeparator)]
    [InlineData("SSN: 321/54/9876", UsSsnRejectionReason.UnsupportedSeparator)]
    [InlineData("SSN: 321_54_9876", UsSsnRejectionReason.UnsupportedSeparator)]
    [InlineData("SSN: 321-54.9876", UsSsnRejectionReason.UnsupportedSeparator)]
    [InlineData("SSN: 321-54 9876", UsSsnRejectionReason.MixedSeparators)] // [EA]
    [InlineData("SSN: 321 54-9876", UsSsnRejectionReason.MixedSeparators)] // [EA]
    public void UnsupportedAndMixedSeparators_AreRejected(string raw, UsSsnRejectionReason reason)
    {
        var result = UsSsnScanner.Detect(raw);

        Assert.Empty(result.Candidates);
        Assert.Contains(result.Rejections, r => r.Reason == reason);
    }

    // ---- 9. SSN / ITIN / ATIN exclusivity (SSN side only) ----

    [Theory]
    [InlineData("912-70-1234")] // ITIN-shaped (9xx, group 70-88)
    [InlineData("SSN: 912-70-1234")]
    [InlineData("950-65-1234")]
    [InlineData("900-93-1234")] // ATIN-shaped (9xx, group 93)
    [InlineData("SSN: 987-93-4321")]
    public void ItinAndAtinShapedNumbers_NeverBecomeSsnCandidates(string raw)
    {
        var result = UsSsnScanner.Detect(raw);

        Assert.Empty(result.Candidates);
        Assert.Contains(result.Rejections, r => r.Reason == UsSsnRejectionReason.Area900To999);
    }

    [Fact]
    public void EveryNineHundredsAreaIsRejected_SoSsnCanNeverOverlapItinOrAtin()
    {
        for (int area = 900; area <= 999; area++)
        {
            var result = UsSsnScanner.Detect($"SSN: {area}-45-6789");

            Assert.Empty(result.Candidates);
            Assert.Contains(result.Rejections, r => r.Reason == UsSsnRejectionReason.Area900To999);
        }

        Assert.Single(UsSsnScanner.Detect("SSN: 899-45-6789").Candidates);
    }

    // ---- 10. empty / null / malformed input ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\0\0\0")]
    [InlineData("\uD800")] // lone surrogate
    [InlineData("SSN: 😀\uD83D")]
    [InlineData("\r\n\r\n")]
    public void NullEmptyAndMalformedInput_IsHandledSafely(string? raw)
    {
        var result = UsSsnScanner.Detect(raw);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void NullLabelSets_AreRejectedExplicitly()
    {
        Assert.Throws<ArgumentNullException>(() => UsSsnScanner.Detect("SSN: 1", null!, []));
        Assert.Throws<ArgumentNullException>(() => UsSsnScanner.Detect("SSN: 1", ["SSN"], null!));
    }

    [Fact]
    public void FieldLengthLimit_96Accepted_97Rejected()
    {
        string accepted = "SSN: 321549876" + new string(' ', 82) + ";"; // label start .. candidate end = 96
        string rejected = "SSN: 321549876" + new string(' ', 83) + ";"; // = 97

        Assert.Single(UsSsnScanner.Detect(accepted).Candidates);
        var over = UsSsnScanner.Detect(rejected);
        Assert.Empty(over.Candidates);
        Assert.Contains(over.Rejections, r => r.Reason == UsSsnRejectionReason.FieldBindingRejected);
    }

    [Fact]
    public void SeparatorSpacesBeyondThree_AreRejectedByTheSharedBinder()
    {
        Assert.Empty(UsSsnScanner.Detect("SSN:    321549876").Candidates); // 4 spaces after ':'
        Assert.Single(UsSsnScanner.Detect("SSN:   321549876").Candidates); // 3 spaces
    }

    [Fact]
    public void VeryLongInput_IsProcessedWithoutFailure()
    {
        string text = string.Concat(Enumerable.Repeat("filler text 321 54 9876 ", 20_000)) + "SSN: 321-54-9876";

        var result = UsSsnScanner.Detect(text);

        Assert.Single(result.Candidates);
    }

    // ---- multiple results and ordering ----

    [Fact]
    public void MultipleNumbers_AreReportedInPositionOrder()
    {
        // The labelled field ends at ';' (a frozen terminator); without it the field would run to the end of the text.
        const string raw = "A 123-45-6789 and SSN: 234567891; then 345-67-8912.";

        var starts = UsSsnScanner.Detect(raw).Candidates.Select(c => c.Span.Start).ToArray();

        Assert.Equal(3, starts.Length);
        Assert.Equal(starts.OrderBy(s => s).ToArray(), starts);
    }

    // ---- 11. determinism, immutability ----

    [Fact]
    public void Detection_IsDeterministic()
    {
        const string raw = "x 321-54-9876 SSN: 000-54-9876 SSN: 321.54.9876 X123-45-6789Y SSN: 234567891";

        var first = UsSsnScanner.Detect(raw);
        var second = UsSsnScanner.Detect(raw);

        Assert.Equal(first.Candidates.Select(c => (c.Span, c.Evidence, c.Form, Digits(c))), second.Candidates.Select(c => (c.Span, c.Evidence, c.Form, Digits(c))));
        Assert.Equal(first.Rejections, second.Rejections);
    }

    [Fact]
    public void ReturnedLists_AreReadOnlyAtRuntime()
    {
        var result = UsSsnScanner.Detect("SSN: 321-54-9876 SSN: 000-54-9876");

        Assert.Throws<NotSupportedException>(() => ((IList<UsSsnCandidate>)result.Candidates).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<UsSsnRejection>)result.Rejections).Clear());
        Assert.NotEmpty(result.Candidates);
        Assert.NotEmpty(result.Rejections);
    }

    [Fact]
    public void DefaultCandidateAndEmptyResult_DoNotLookLikeFindings()
    {
        Assert.Empty(UsSsnScanResult.Empty.Candidates);
        Assert.Empty(UsSsnScanResult.Empty.Rejections);
        Assert.Equal(string.Empty, default(UsSsnCanonical).GetDigitsForTestingOnly());
    }

    // ---- 12. no raw personal data in any diagnostic string ----

    [Fact]
    public void Diagnostics_NeverContainTheNumberOrItsDigits()
    {
        const string raw = "SSN: 321-54-9876 and SSN: 000-54-9876 and 777-12-3456";
        var result = UsSsnScanner.Detect(raw);

        var texts = new List<string> { result.ToString() };
        texts.AddRange(result.Candidates.Select(c => c.ToString()));
        texts.AddRange(result.Candidates.Select(c => c.Canonical.ToString()));
        texts.AddRange(result.Rejections.Select(r => r.ToString()));

        foreach (var text in texts)
        {
            Assert.DoesNotContain("321", text);
            Assert.DoesNotContain("9876", text);
            Assert.DoesNotContain("777", text);
            Assert.DoesNotContain("3456", text);
            Assert.DoesNotContain("54", text);
        }
    }

    [Fact]
    public void ResultNotice_StatesCandidatesOnlyAndNotASecurityDetermination()
    {
        // After the promotion into the product the notice no longer claims "experiment only / not registered": it still says a result is a candidate list, not a determination.
        Assert.Contains("CANDIDATES_ONLY", UsSsnScanResult.Notice);
        Assert.Contains("NOT_A_SECURITY_DETERMINATION", UsSsnScanResult.Notice);
        Assert.DoesNotContain("EXPERIMENT", UsSsnScanResult.Notice);
    }
}
