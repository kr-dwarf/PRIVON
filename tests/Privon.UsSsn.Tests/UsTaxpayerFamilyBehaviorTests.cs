using Privon.Detection;
using Privon.UsSsn;

namespace Privon.UsSsn.Tests;

// SYNTHETIC DATA ONLY. EXPERIMENT ONLY. Cases marked [EA] pin an EXPERIMENT ASSUMPTION for a branch the frozen
// corpus does not decide; they must not be read as the official rule.
public class UsTaxpayerFamilyBehaviorTests
{
    private static UsTaxpayerKind? OnlyKind(string raw)
    {
        var result = UsTaxpayerFamily.Evaluate(raw);
        Assert.True(result.Candidates.Count <= 1, "one value must never be emitted as more than one type");
        return result.Candidates.Count == 0 ? null : result.Candidates[0].Kind;
    }

    // ---- ITIN group boundaries: both edges of each accepted range and the value just outside it ----

    [Theory]
    [InlineData(49, false)]
    [InlineData(50, true)]
    [InlineData(65, true)]
    [InlineData(66, false)]
    [InlineData(69, false)]
    [InlineData(70, true)]
    [InlineData(88, true)]
    [InlineData(89, false)]
    [InlineData(90, true)]
    [InlineData(92, true)]
    [InlineData(93, false)]
    [InlineData(94, true)]
    [InlineData(99, true)]
    [InlineData(0, false)]
    public void ItinGroupRanges_AreExactlyTheFrozenSet(int group, bool accepted)
    {
        var kind = OnlyKind($"ITIN: 912-{group:D2}-1234");

        if (accepted) Assert.Equal(UsTaxpayerKind.Itin, kind);
        else Assert.NotEqual(UsTaxpayerKind.Itin, kind);
    }

    [Theory]
    [InlineData("ITIN: 900-70-1234", true)]
    [InlineData("ITIN: 999-70-1234", true)]
    [InlineData("ITIN: 899-70-1234", false)]
    [InlineData("ITIN: 123-70-1234", false)]
    [InlineData("ITIN: 000-70-1234", false)]
    public void ItinAreaMustStartWithNine(string raw, bool accepted)
    {
        Assert.Equal(accepted, OnlyKind(raw) == UsTaxpayerKind.Itin);
        if (!accepted)
        {
            Assert.Contains(UsTaxpayerFamily.Evaluate(raw).Attempts,
                a => a.Kind == UsTaxpayerKind.Itin && a.Reason == UsTaxpayerRejectionReason.ItinAreaNotNine);
        }
    }

    // ---- ATIN: 9xx area and group 93 only; near misses fail ----

    [Theory]
    [InlineData(91, false)]
    [InlineData(92, false)]
    [InlineData(93, true)]
    [InlineData(94, false)]
    [InlineData(39, false)]
    [InlineData(3, false)]
    public void AtinRequiresGroup93(int group, bool accepted)
    {
        Assert.Equal(accepted, OnlyKind($"ATIN: 912-{group:D2}-1234") == UsTaxpayerKind.Atin);
    }

    [Theory]
    [InlineData("ATIN: 899-93-1234", false)]
    [InlineData("ATIN: 900-93-1234", true)]
    [InlineData("ATIN: 999-93-1234", true)]
    [InlineData("ATIN: 321-93-1234", false)]
    public void AtinAreaMustStartWithNine(string raw, bool accepted)
    {
        Assert.Equal(accepted, OnlyKind(raw) == UsTaxpayerKind.Atin);
    }

    // ---- exclusivity: for every area/group the three types never overlap, and the decision matches an independent table ----

    private static UsTaxpayerKind? ExpectedKind(int area, int group, int serial)
    {
        bool nine = area >= 900;
        if (nine && group == 93) return UsTaxpayerKind.Atin;
        if (nine && ((group >= 50 && group <= 65) || (group >= 70 && group <= 88) || (group >= 90 && group <= 92) || (group >= 94 && group <= 99)))
            return UsTaxpayerKind.Itin;
        if (!nine && area != 0 && area != 666 && group != 0 && serial != 0) return UsTaxpayerKind.Ssn;
        return null;
    }

    [Fact]
    public void ExhaustiveGroupSweep_AtMostOneTypeAndTheExpectedType()
    {
        foreach (int area in new[] { 0, 1, 665, 666, 667, 899, 900, 912, 999 })
        foreach (int serial in new[] { 0, 1, 1234 })
        {
            for (int group = 0; group <= 99; group++)
            {
                string raw = $"{area:D3}-{group:D2}-{serial:D4}"; // unlabeled dashed: structure only
                var result = UsTaxpayerFamily.Evaluate(raw);

                Assert.True(result.Candidates.Count <= 1, raw);
                Assert.Equal(ExpectedKind(area, group, serial), result.Candidates.Count == 0 ? null : result.Candidates[0].Kind);
            }
        }
    }

    // ---- priority and mutual exclusion ----

    [Fact]
    public void Arbiter_OnTheSameSpan_AtinBeatsItinBeatsSsn_RegardlessOfInputOrder()
    {
        var span = new RawSpan(0, 11);
        UsTaxpayerCandidate Make(UsTaxpayerKind kind) =>
            UsTaxpayerFamily.CreateCandidateForTesting(kind, span, UsSsnEvidence.Structure, UsSsnForm.Dashed, "912934567");
        var all = new[] { Make(UsTaxpayerKind.Ssn), Make(UsTaxpayerKind.Itin), Make(UsTaxpayerKind.Atin) };

        foreach (var order in Permutations(all))
        {
            var (emitted, superseded) = UsTaxpayerArbiter.Resolve(order);

            Assert.Equal(UsTaxpayerKind.Atin, Assert.Single(emitted).Kind);
            Assert.Equal(
                [UsTaxpayerKind.Itin, UsTaxpayerKind.Ssn],
                superseded.Select(c => c.Kind).OrderBy(k => (int)k).ToArray());
        }
    }

    [Fact]
    public void Arbiter_ItinBeatsSsn_AndOverlappingSpansAreExclusive()
    {
        UsTaxpayerCandidate Make(UsTaxpayerKind kind, int start, int length) =>
            UsTaxpayerFamily.CreateCandidateForTesting(kind, new RawSpan(start, length), UsSsnEvidence.Structure, UsSsnForm.Dashed, "123456789");

        var (emitted, superseded) = UsTaxpayerArbiter.Resolve(
            [Make(UsTaxpayerKind.Ssn, 0, 11), Make(UsTaxpayerKind.Itin, 4, 11), Make(UsTaxpayerKind.Ssn, 30, 11)]);

        Assert.Equal([UsTaxpayerKind.Itin, UsTaxpayerKind.Ssn], emitted.Select(c => c.Kind).ToArray());
        Assert.Equal(new RawSpan(4, 11), emitted[0].Span);
        Assert.Equal(new RawSpan(30, 11), emitted[1].Span);
        Assert.Equal(new RawSpan(0, 11), Assert.Single(superseded).Span);
    }

    [Fact]
    public void Arbiter_HandlesEmptyDuplicatesAndNull()
    {
        var one = UsTaxpayerFamily.CreateCandidateForTesting(UsTaxpayerKind.Ssn, new RawSpan(0, 11), UsSsnEvidence.Structure, UsSsnForm.Dashed, "123456789");

        Assert.Empty(UsTaxpayerArbiter.Resolve([]).Emitted);
        Assert.Single(UsTaxpayerArbiter.Resolve([one, one]).Emitted);
        Assert.Throws<ArgumentNullException>(() => UsTaxpayerArbiter.Resolve(null!));
    }

    private static IEnumerable<T[]> Permutations<T>(T[] items)
    {
        if (items.Length <= 1) { yield return items; yield break; }
        for (int i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, index) => index != i).ToArray();
            foreach (var tail in Permutations(rest)) yield return [items[i], .. tail];
        }
    }

    // ---- labels, terminators, other-kind labels ----

    [Theory]
    [InlineData("itin: 912-70-1234", UsTaxpayerKind.Itin)]
    [InlineData("Atin=912934567", UsTaxpayerKind.Atin)]
    [InlineData("ATIN #912 93 4567", UsTaxpayerKind.Atin)]
    [InlineData("ITIN 912701234", UsTaxpayerKind.Itin)]
    [InlineData("x\nITIN: 912-70-1234;SSN: 321-54-9876", UsTaxpayerKind.Itin)]
    public void LabelledValues_AreRecognizedByTheirOwnLabel(string raw, UsTaxpayerKind kind)
    {
        Assert.Equal(kind, UsTaxpayerFamily.Evaluate(raw).Candidates.First().Kind);
    }

    [Theory]
    [InlineData("ITIN: 912701234\rmemo")]
    [InlineData("ITIN: 912701234\nmemo")]
    [InlineData("ITIN: 912701234;memo")]
    [InlineData("ITIN: 912701234\tmemo")]
    [InlineData("ITIN: 912701234|memo")]
    public void FieldTerminators_EndTheValue(string raw)
    {
        var candidate = Assert.Single(UsTaxpayerFamily.Evaluate(raw).Candidates);

        Assert.Equal(new RawSpan(6, 9), candidate.Span);
        Assert.Equal(UsTaxpayerKind.Itin, candidate.Kind);
    }

    [Fact]
    public void AnotherRecognizedLabel_EndsTheValue_AndBothFieldsAreEmitted()
    {
        var result = UsTaxpayerFamily.Evaluate("SSN: 321549876 ITIN: 912701234");

        Assert.Equal([UsTaxpayerKind.Ssn, UsTaxpayerKind.Itin], result.Candidates.Select(c => c.Kind).ToArray());
        Assert.All(result.Candidates, c => Assert.Equal(UsSsnEvidence.Structure | UsSsnEvidence.Context, c.Evidence));
    }

    [Fact]
    public void AdjacentTaxLabels_TheSecondLabelStillBindsItsOwnValue()
    {
        var result = UsTaxpayerFamily.Evaluate("SSN: ITIN: 912-70-1234");

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(UsTaxpayerKind.Itin, candidate.Kind);
        Assert.Equal(UsSsnEvidence.Structure | UsSsnEvidence.Context, candidate.Evidence);
        Assert.Contains(UsTaxpayerRejectionReason.NextRecognizedFieldTerminatesBinding,
            result.Attempts.Where(a => a.Kind == UsTaxpayerKind.Ssn).Select(a => a.Reason!.Value));
    }

    [Theory] // [EA] a value under a DIFFERENT tax label is judged by structure alone, as an unlabeled dashed number
    [InlineData("SSN: 912-70-1234", UsTaxpayerKind.Itin)]
    [InlineData("ITIN: 912-93-4567", UsTaxpayerKind.Atin)]
    [InlineData("ATIN: 912-70-1234", UsTaxpayerKind.Itin)]
    [InlineData("ITIN: 321-54-9876", UsTaxpayerKind.Ssn)]
    public void ValueUnderAnotherTaxLabel_IsClassifiedByStructureOnly(string raw, UsTaxpayerKind kind)
    {
        var candidate = Assert.Single(UsTaxpayerFamily.Evaluate(raw).Candidates);

        Assert.Equal(kind, candidate.Kind);
        Assert.Equal(UsSsnEvidence.Structure, candidate.Evidence);
    }

    [Fact]
    public void LabelsInsideWords_AreNotLabels()
    {
        Assert.Empty(UsTaxpayerFamily.Evaluate("MATIN: 912934567").Candidates);
        Assert.Empty(UsTaxpayerFamily.Evaluate("SITIN: 912701234").Candidates);
        Assert.Empty(UsTaxpayerFamily.Evaluate("ITINX: 912701234").Candidates);
    }

    [Fact]
    public void UnlabeledCompactAndSpacedNumbers_AreNeverEmitted()
    {
        foreach (var raw in new[] { "912701234", "912934567", "321549876", "912 70 1234", "912 93 4567", "321 54 9876" })
        {
            Assert.Empty(UsTaxpayerFamily.Evaluate(raw).Candidates);
        }
    }

    [Fact]
    public void TextAfterAnUnterminatedLabelledValue_IsRejected() // [EA] same rule as SSN
    {
        var result = UsTaxpayerFamily.Evaluate("ATIN: 912934567 thanks");

        Assert.Empty(result.Candidates);
        Assert.Contains(UsTaxpayerRejectionReason.TrailingContent, result.Attempts.Select(a => a.Reason!.Value));
    }

    // ---- unicode, boundaries, separators, length ----

    [Theory]
    [InlineData("ITIN: ９１２-70-1234")]
    [InlineData("ATIN: 912-٩٣-1234")]
    [InlineData("912-70-123٤")]
    [InlineData("ATIN: ９１２９３４５６７")]
    public void NonAsciiDigits_AreRejected_NeverFolded(string raw)
    {
        var result = UsTaxpayerFamily.Evaluate(raw);

        Assert.Empty(result.Candidates);
        Assert.Contains(UsTaxpayerRejectionReason.NonAsciiDigit, result.Attempts.Select(a => a.Reason!.Value));
    }

    [Theory]
    [InlineData("a912-70-1234")]
    [InlineData("912-70-1234b")]
    [InlineData("1912-70-1234")]
    [InlineData("912-70-12345")]
    [InlineData("-912-93-1234")]
    [InlineData("912-93-1234-5")]
    [InlineData("ITIN: 9127012345")]
    [InlineData("ATIN: x912934567")]
    public void NumbersTouchingOtherCharacters_AreBoundaryViolations(string raw)
    {
        var result = UsTaxpayerFamily.Evaluate(raw);

        Assert.Empty(result.Candidates);
        Assert.Contains(UsTaxpayerRejectionReason.AlphanumericBoundaryViolation, result.Attempts.Select(a => a.Reason!.Value));
    }

    [Theory]
    [InlineData("ITIN: 912-70 1234", UsTaxpayerRejectionReason.MixedSeparators)]
    [InlineData("ATIN: 912 93-4567", UsTaxpayerRejectionReason.MixedSeparators)]
    [InlineData("ITIN: 912/70/1234", UsTaxpayerRejectionReason.UnsupportedSeparator)]
    [InlineData("ATIN: 912_93_4567", UsTaxpayerRejectionReason.UnsupportedSeparator)]
    [InlineData("ITIN: 912-70-123", UsTaxpayerRejectionReason.InvalidLength)]
    [InlineData("ATIN: 91-293-4567", UsTaxpayerRejectionReason.InvalidLength)]
    public void SeparatorAndLengthDefects_AreRejectedWithTheirReason(string raw, UsTaxpayerRejectionReason reason)
    {
        var result = UsTaxpayerFamily.Evaluate(raw);

        Assert.Empty(result.Candidates);
        Assert.Contains(reason, result.Attempts.Select(a => a.Reason!.Value));
    }

    [Fact]
    public void FieldLengthLimit_96Accepted_97Rejected()
    {
        string accepted = "ITIN: 912701234" + new string(' ', 81) + ";"; // label start .. candidate end = 96
        string rejected = "ITIN: 912701234" + new string(' ', 82) + ";"; // = 97

        Assert.Single(UsTaxpayerFamily.Evaluate(accepted).Candidates);
        var over = UsTaxpayerFamily.Evaluate(rejected);
        Assert.Empty(over.Candidates);
        Assert.Contains(UsTaxpayerRejectionReason.FieldBindingRejected, over.Attempts.Select(a => a.Reason!.Value));
    }

    // ---- spans are exact raw spans ----

    [Fact]
    public void EmittedSpans_AreTheExactRawText()
    {
        const string raw = "ref ITIN:  912-70-1234 ; SSN 321549876 ; also 912-93-4567.";
        var result = UsTaxpayerFamily.Evaluate(raw);

        Assert.Equal(
            ["912-70-1234", "321549876", "912-93-4567"],
            result.Candidates.Select(c => raw.Substring(c.Span.Start, c.Span.Length)).ToArray());
        Assert.Equal([UsTaxpayerKind.Itin, UsTaxpayerKind.Ssn, UsTaxpayerKind.Atin], result.Candidates.Select(c => c.Kind).ToArray());
    }

    // ---- fail-closed on bad input ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\0\0\0")]
    [InlineData("\uD800")]
    [InlineData("ITIN: \uD83D")]
    public void NullEmptyAndMalformedInput_IsHandledSafely(string? raw)
    {
        var result = UsTaxpayerFamily.Evaluate(raw);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void NullLabelSet_IsRejectedExplicitly()
    {
        Assert.Throws<ArgumentNullException>(() => UsTaxpayerFamily.Evaluate("ITIN: 912701234", null!));
    }

    [Fact]
    public void VeryLongInput_IsProcessedWithoutFailure()
    {
        string text = string.Concat(Enumerable.Repeat("filler 912 70 1234 text ", 20_000)) + "ITIN: 912-70-1234";

        Assert.Single(UsTaxpayerFamily.Evaluate(text).Candidates);
    }

    // ---- reuse of the existing SSN behavior: family SSN output == the standalone SSN module ----

    [Theory]
    [InlineData("SSN: 321-54-9876")]
    [InlineData("321-54-9876")]
    [InlineData("SSN: 321549876")]
    [InlineData("Social Security Number: 321 54 9876")]
    [InlineData("SSN: 000-54-9876")]
    [InlineData("SSN: 321.54.9876")]
    [InlineData("SSN: 321-54-987６")]
    [InlineData("X321-54-9876Y")]
    [InlineData("SSN: 321549876;memo")]
    [InlineData("CLASSN: 321549876")]
    [InlineData("call 321-54-9876 now, SSN: 234567891;")]
    [InlineData("321549876")]
    public void SsnOnlyInputs_GiveTheSameCandidatesAsTheStandaloneSsnModule(string raw)
    {
        var standalone = UsSsnScanner.Detect(raw).Candidates
            .Select(c => (c.Span, c.Evidence, c.Form, c.Canonical.GetDigitsForTestingOnly())).ToArray();
        var family = UsTaxpayerFamily.Evaluate(raw).Candidates.Where(c => c.Kind == UsTaxpayerKind.Ssn)
            .Select(c => (c.Span, c.Evidence, c.Form, c.Canonical.GetDigitsForTestingOnly())).ToArray();

        Assert.Equal(standalone, family);
    }

    // ---- determinism, immutability, redaction ----

    [Fact]
    public void Evaluation_IsDeterministic()
    {
        const string raw = "x 912-93-4567 ITIN: 912-70-1234; SSN: 000-54-9876 ATIN: 912.93.1234 321-54-9876";

        var first = UsTaxpayerFamily.Evaluate(raw);
        var second = UsTaxpayerFamily.Evaluate(raw);

        Assert.Equal(first.Candidates.Select(c => (c.Kind, c.Span, c.Evidence, c.Form)), second.Candidates.Select(c => (c.Kind, c.Span, c.Evidence, c.Form)));
        Assert.Equal(first.Attempts, second.Attempts);
    }

    [Fact]
    public void ReturnedLists_AreReadOnlyAtRuntime()
    {
        var result = UsTaxpayerFamily.Evaluate("ITIN: 912-70-1234; SSN: 000-54-9876");

        Assert.Throws<NotSupportedException>(() => ((IList<UsTaxpayerCandidate>)result.Candidates).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<UsTaxpayerAttempt>)result.Attempts).Clear());
        Assert.NotEmpty(result.Candidates);
        Assert.NotEmpty(result.Attempts);
    }

    [Fact]
    public void Diagnostics_NeverContainTheNumberOrItsDigits()
    {
        const string raw = "ITIN: 912-70-1234; ATIN: 987-93-6543; SSN: 321-54-9876; SSN: 000-12-3456";
        var result = UsTaxpayerFamily.Evaluate(raw);

        var texts = new List<string> { result.ToString() };
        texts.AddRange(result.Candidates.Select(c => c.ToString()));
        texts.AddRange(result.Candidates.Select(c => c.Canonical.ToString()));
        texts.AddRange(result.Attempts.Select(a => a.ToString()));

        foreach (var text in texts)
        {
            // "93" and "000" are omitted on purpose: they are rule constants that legitimately appear in reason names
            // (AtinRequires93, Area000). Every digit string below can only have come from the input.
            foreach (var secret in new[] { "912", "987", "321", "1234", "6543", "9876", "3456", "70", "54" })
            {
                Assert.DoesNotContain(secret, text);
            }
        }
    }

    [Fact]
    public void ResultNotice_StatesCandidatesOnlyAndNotASecurityDetermination()
    {
        Assert.Contains("CANDIDATES_ONLY", UsTaxpayerFamilyResult.Notice);
        Assert.Contains("NOT_A_SECURITY_DETERMINATION", UsTaxpayerFamilyResult.Notice);
        Assert.DoesNotContain("EXPERIMENT", UsTaxpayerFamilyResult.Notice);
    }

    [Fact]
    public void EmptyResult_AndDefaultCandidate_DoNotLookLikeFindings()
    {
        Assert.Empty(UsTaxpayerFamilyResult.Empty.Candidates);
        Assert.Empty(UsTaxpayerFamilyResult.Empty.Attempts);
    }
}
