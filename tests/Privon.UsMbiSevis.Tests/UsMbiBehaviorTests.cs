using System.Diagnostics;
using System.Text.RegularExpressions;
using Privon.UsMbiSevis;

namespace Privon.UsMbiSevis.Tests;

// SYNTHETIC DATA ONLY. Behaviour beyond the frozen D3A corpus. Where the contract (D2.2) is silent the expectation is
// marked [EA] (experiment assumption); such tests document what this experiment does, not what the official rule is.
public class UsMbiBehaviorTests
{
    private const string Valid = "1A23C45DE67";   // synthetic; satisfies all 11 position rules
    private const string Valid2 = "9Y99Y99YY99";   // synthetic; satisfies all 11 position rules

    // Independent oracle written straight from the D2.2 position list (not from the implementation).
    private static readonly Regex Contract = new(
        "^[1-9][ACDEFGHJKMNPQRTUVWXY][0-9ACDEFGHJKMNPQRTUVWXY][0-9][ACDEFGHJKMNPQRTUVWXY][0-9ACDEFGHJKMNPQRTUVWXY][0-9]"
        + "[ACDEFGHJKMNPQRTUVWXY]{2}[0-9]{2}$",
        RegexOptions.CultureInvariant);

    private const string AsciiAlnum = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private static string With(int position1Based, char c) =>
        Valid[..(position1Based - 1)] + c + Valid[position1Based..];

    private static string Describe(UsMbiCandidate c) =>
        $"{(c.Evidence == (UsIdEvidence.Structure | UsIdEvidence.Context) ? "S+C" : c.Evidence == UsIdEvidence.Structure ? "S" : "?")}"
        + $"|{c.Span.Start}:{c.Span.Length}|{c.Form}|{c.Canonical.GetValueForTestingOnly()}";

    // ---- position grammar -----------------------------------------------------------------------------------------

    [Fact]
    public void PositionGrammar_AgreesWithAnIndependentContractRegex_ForEveryPositionAndEveryAsciiAlphanumeric()
    {
        var mismatches = new List<string>();
        for (int p = 1; p <= 11; p++)
        {
            foreach (char c in AsciiAlnum)
            {
                string s = With(p, c);
                int expected = Contract.IsMatch(s.ToUpperInvariant()) ? 1 : 0;

                int bare = UsMbiScanner.Detect(s).Candidates.Count;
                int unlabelled = UsMbiScanner.Detect("ref " + s + " end").Candidates.Count;
                int labelled = UsMbiScanner.Detect("MBI: " + s).Candidates.Count;
                if (bare != expected || unlabelled != expected || labelled != expected)
                {
                    mismatches.Add($"p{p}/{c}");
                }
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void AllowedLetterSet_IsExactlyTheTwentyContractLetters_UpperAndLowerCase()
    {
        const string allowed = "ACDEFGHJKMNPQRTUVWXY";
        const string excluded = "BILOSZ";
        Assert.Equal(26, allowed.Length + excluded.Length);

        foreach (char c in allowed)
        {
            Assert.Single(UsMbiScanner.Detect(With(2, c)).Candidates);
            Assert.Single(UsMbiScanner.Detect(With(2, char.ToLowerInvariant(c))).Candidates);
        }

        foreach (char c in excluded)
        {
            Assert.Empty(UsMbiScanner.Detect(With(2, c)).Candidates);
            Assert.Empty(UsMbiScanner.Detect(With(2, char.ToLowerInvariant(c))).Candidates);
        }
    }

    [Fact]
    public void FirstPosition_AcceptsOneToNine_AndNothingElse()
    {
        foreach (char c in "123456789")
        {
            Assert.Single(UsMbiScanner.Detect(With(1, c)).Candidates);
        }

        foreach (char c in "0ABCDEFGHJKMNPQRTUVWXYZ")
        {
            Assert.Empty(UsMbiScanner.Detect(With(1, c)).Candidates);
        }
    }

    [Theory]
    [InlineData(1, '0', UsMbiRejectionReason.Position1RequiresOneToNine)]
    [InlineData(1, 'A', UsMbiRejectionReason.Position1RequiresOneToNine)]
    [InlineData(2, '5', UsMbiRejectionReason.PositionRequiresAllowedLetter)]
    [InlineData(5, '5', UsMbiRejectionReason.PositionRequiresAllowedLetter)]
    [InlineData(8, '5', UsMbiRejectionReason.PositionRequiresAllowedLetter)]
    [InlineData(9, '5', UsMbiRejectionReason.PositionRequiresAllowedLetter)]
    [InlineData(2, 'B', UsMbiRejectionReason.ExcludedB)]
    [InlineData(5, 'I', UsMbiRejectionReason.ExcludedI)]
    [InlineData(8, 'L', UsMbiRejectionReason.ExcludedL)]
    [InlineData(9, 'O', UsMbiRejectionReason.ExcludedO)]
    [InlineData(3, 'S', UsMbiRejectionReason.ExcludedS)]
    [InlineData(6, 'Z', UsMbiRejectionReason.ExcludedZ)]
    [InlineData(6, 'o', UsMbiRejectionReason.ExcludedO)]
    [InlineData(4, 'A', UsMbiRejectionReason.PositionRequiresDigit)]
    [InlineData(4, 'B', UsMbiRejectionReason.PositionRequiresDigit)]
    [InlineData(7, 'C', UsMbiRejectionReason.PositionRequiresDigit)]
    [InlineData(10, 'D', UsMbiRejectionReason.PositionRequiresDigit)]
    [InlineData(11, 'Z', UsMbiRejectionReason.PositionRequiresDigit)]
    public void PositionViolations_ReportTheReasonAndThe1BasedPosition(int position, char c, UsMbiRejectionReason reason)
    {
        var result = UsMbiScanner.Detect("MBI: " + With(position, c));

        Assert.Empty(result.Candidates);
        var rejection = Assert.Single(result.Rejections);
        Assert.Equal(reason, rejection.Reason);
        Assert.Equal(position, rejection.Position);
    }

    [Fact]
    public void WhenSeveralPositionsFail_TheFirstFailingPositionIsReported_EaOrdering()
    {
        string both = "1B2AC45DE67"; // position 2 (excluded B) and position 4 (letter where a digit is required)

        var rejection = Assert.Single(UsMbiScanner.Detect("MBI: " + both).Rejections);

        Assert.Equal(UsMbiRejectionReason.ExcludedB, rejection.Reason);
        Assert.Equal(2, rejection.Position);
    }

    // ---- display forms --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1A23C45DE67", UsMbiForm.Compact, 11)]
    [InlineData("1A23-C45-DE67", UsMbiForm.Hyphenated, 13)]
    [InlineData("1A23 C45 DE67", UsMbiForm.Spaced, 13)]
    [InlineData("1a23-c45-de67", UsMbiForm.Hyphenated, 13)]
    [InlineData("1a23 c45 de67", UsMbiForm.Spaced, 13)]
    [InlineData("1a23C45dE67", UsMbiForm.Compact, 11)]
    public void AcceptedDisplayForms_ProduceTheNormalisedUpperCaseCanonical_WithOrWithoutLabel(
        string display, UsMbiForm form, int length)
    {
        var labelled = Assert.Single(UsMbiScanner.Detect("MBI: " + display).Candidates);
        var bare = Assert.Single(UsMbiScanner.Detect("x " + display + " y").Candidates);

        Assert.Equal($"S+C|5:{length}|{form}|1A23C45DE67", Describe(labelled));
        Assert.Equal($"S|2:{length}|{form}|1A23C45DE67", Describe(bare));
    }

    [Theory]
    [InlineData("1A23--C45DE67", UsMbiRejectionReason.BadGrouping)]
    [InlineData("1A23C-45-DE67", UsMbiRejectionReason.BadGrouping)]
    [InlineData("1A23-C45DE-67", UsMbiRejectionReason.BadGrouping)]
    [InlineData("1A23.C45.DE67", UsMbiRejectionReason.UnsupportedSeparator)]
    [InlineData("1A23_C45_DE67", UsMbiRejectionReason.UnsupportedSeparator)]
    [InlineData("1A23/C45/DE67", UsMbiRejectionReason.UnsupportedSeparator)]
    [InlineData("1A23-C45.DE67", UsMbiRejectionReason.UnsupportedSeparator)]
    [InlineData("1A23 C45 DE67", UsMbiRejectionReason.UnsupportedSeparator)]
    [InlineData("1A23–C45–DE67", UsMbiRejectionReason.UnsupportedSeparator)]
    [InlineData("1A23－C45－DE67", UsMbiRejectionReason.UnsupportedSeparator)]
    [InlineData("1A23-C45-DE6", UsMbiRejectionReason.WrongLength)]
    [InlineData("1A23-C45-DE678", UsMbiRejectionReason.WrongLength)]
    [InlineData("1A23C45DE678", UsMbiRejectionReason.WrongLength)]
    [InlineData("1A23C45DE", UsMbiRejectionReason.WrongLength)]
    [InlineData("1A-23C-45D-E67", UsMbiRejectionReason.UnrecognizedValueShape)]
    public void RejectedShapes_ProduceNoCandidateAndTheSpecificReason(string value, UsMbiRejectionReason reason)
    {
        var result = UsMbiScanner.Detect("MBI: " + value);

        Assert.Empty(result.Candidates);
        Assert.Contains(result.Rejections, r => r.Reason == reason);
    }

    // ---- boundaries -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", "")]
    [InlineData(" ", " ")]
    [InlineData("(", ")")]
    [InlineData("[", "]")]
    [InlineData("\"", "\"")]
    [InlineData("'", "'")]
    [InlineData(",", ".")]
    [InlineData(":", ";")]
    [InlineData("_", "_")]
    [InlineData("/", "/")]
    [InlineData("-", "-")] // [EA] contract-literal: a hyphen is a non-alphanumeric boundary
    [InlineData("\n", "\r")]
    [InlineData("\t", "|")]
    [InlineData("\U0001F600", "\U0001F600")]
    public void NonAlphanumericNeighbours_AreValidBoundaries(string before, string after)
    {
        foreach (string display in new[] { Valid, "1A23-C45-DE67", "1A23 C45 DE67" })
        {
            var result = UsMbiScanner.Detect(before + display + after);

            var candidate = Assert.Single(result.Candidates);
            Assert.Equal(before.Length, candidate.Span.Start);
            Assert.Equal(display.Length, candidate.Span.Length);
        }
    }

    [Theory]
    [InlineData("X", "")]
    [InlineData("x", "")]
    [InlineData("7", "")]
    [InlineData("é", "")]  // é
    [InlineData("А", "")]  // Cyrillic A
    [InlineData("１", "")]  // fullwidth 1
    [InlineData("٣", "")]  // Arabic-Indic 3
    [InlineData("", "X")]
    [InlineData("", "x")]
    [InlineData("", "9")]
    [InlineData("", "é")]
    [InlineData("", "А")]
    [InlineData("", "１")]
    [InlineData("", "٣")]
    [InlineData("X", "Y")]
    public void AlphanumericNeighbours_AreBoundaryViolations_IncludingNonAsciiLetterAndDigitLookalikes(string before, string after)
    {
        foreach (string display in new[] { Valid, "1A23-C45-DE67", "1A23 C45 DE67" })
        {
            var result = UsMbiScanner.Detect(before + display + after);

            Assert.Empty(result.Candidates);
            Assert.Contains(result.Rejections, r => r.Reason == UsMbiRejectionReason.BoundaryViolation);
        }
    }

    [Fact]
    public void SeveralMbisInOneText_AreAllReportedInOrder_IncludingIdenticalValues()
    {
        string text = $"a {Valid}, b {Valid2}; c {Valid} d 9Y99-Y99-YY99";

        var result = UsMbiScanner.Detect(text);

        Assert.Equal(
            new[] { "S|2:11|Compact|1A23C45DE67", "S|17:11|Compact|9Y99Y99YY99", "S|32:11|Compact|1A23C45DE67", "S|46:13|Hyphenated|9Y99Y99YY99" },
            result.Candidates.Select(Describe));
    }

    [Fact]
    public void AdjacentMbisSeparatedBySingleSpace_AreTwoCandidates()
    {
        var result = UsMbiScanner.Detect($"{Valid} {Valid2}");

        Assert.Equal(
            new[] { "0:11", "12:11" },
            result.Candidates.Select(c => $"{c.Span.Start}:{c.Span.Length}"));
    }

    // ---- unicode lookalikes ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2, 'Ａ')]   // fullwidth A
    [InlineData(2, 'А')]   // Cyrillic A
    [InlineData(3, 'K')]   // Kelvin sign (folds to K under some case-insensitive comparers)
    [InlineData(5, 'С')]   // Cyrillic Es (looks like C)
    [InlineData(10, '١')]  // Arabic-Indic digit one
    [InlineData(11, '７')]  // fullwidth 7
    [InlineData(1, '１')]   // fullwidth 1
    public void NonAsciiCharacters_NeverCountAsMbiCharacters(int position, char lookalike)
    {
        foreach (string value in new[] { With(position, lookalike), With(position, lookalike).Insert(4, "-").Insert(8, "-") })
        {
            var labelled = UsMbiScanner.Detect("MBI: " + value);
            var bare = UsMbiScanner.Detect(value);

            Assert.Empty(labelled.Candidates);
            Assert.Empty(bare.Candidates);
            Assert.Contains(labelled.Rejections, r => r.Reason == UsMbiRejectionReason.NonAsciiLookalike);
        }
    }

    // ---- labels and field binding ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("MBI:1A23C45DE67", 4)]
    [InlineData("MBI : 1A23C45DE67", 6)]
    [InlineData("mbi: 1A23C45DE67", 5)]
    [InlineData("Mbi= 1A23C45DE67", 5)]
    [InlineData("MBI # 1A23C45DE67", 6)]
    [InlineData("MBI 1A23C45DE67", 4)]
    [InlineData("MBI   :   1A23C45DE67", 10)]
    public void LabelledValue_GetsStructureAndContext_AtTheExactValueSpan(string raw, int valueStart)
    {
        var candidate = Assert.Single(UsMbiScanner.Detect(raw).Candidates);

        Assert.Equal($"S+C|{valueStart}:11|Compact|1A23C45DE67", Describe(candidate));
    }

    [Theory]
    [InlineData("MBI    : 1A23C45DE67")]   // 4 spaces before the separator
    [InlineData("MBI:     1A23C45DE67")]   // 4 spaces after it
    public void WhenTheFieldBindingIsRejected_TheValueIsStillAnMbiByStructureAlone(string raw)
    {
        var result = UsMbiScanner.Detect(raw);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(UsIdEvidence.Structure, candidate.Evidence); // the contract makes an unlabelled valid MBI Level 3
        Assert.Contains(result.Rejections, r => r.Reason == UsMbiRejectionReason.FieldBindingRejected);
    }

    [Theory]
    [InlineData('\r')]
    [InlineData('\n')]
    [InlineData(';')]
    [InlineData('\t')]
    [InlineData('|')]
    public void FieldTerminators_EndTheValue(char terminator)
    {
        var candidate = Assert.Single(UsMbiScanner.Detect($"MBI: {Valid}{terminator}memo").Candidates);

        Assert.Equal("S+C|5:11|Compact|1A23C45DE67", Describe(candidate));
    }

    [Fact]
    public void NextRecognizedLabel_IsUsedOnlyWhenTheCallerSuppliesIt()
    {
        const string raw = "MBI: 1A23C45DE67 Phone: 2125551212";

        var with = UsMbiScanner.Detect(raw, UsMbiScanner.DefaultStrongLabels, ["Phone"]);
        var without = UsMbiScanner.Detect(raw);

        Assert.Equal("S+C|5:11|Compact|1A23C45DE67", Describe(Assert.Single(with.Candidates)));
        Assert.Empty(with.Rejections);
        // [EA] the field then runs on past the value: context is not credited, but the structure alone still matches.
        Assert.Equal("S|5:11|Compact|1A23C45DE67", Describe(Assert.Single(without.Candidates)));
        Assert.Contains(without.Rejections, r => r.Reason == UsMbiRejectionReason.TrailingContent);
    }

    [Theory]
    [InlineData("XMBI: 1A23C45DE67")]
    [InlineData("MBIX: 1A23C45DE67")]
    [InlineData("9MBI: 1A23C45DE67")]
    public void ALabelInsideAWord_IsNotALabel_EA(string raw)
    {
        var candidate = Assert.Single(UsMbiScanner.Detect(raw).Candidates);

        Assert.Equal(UsIdEvidence.Structure, candidate.Evidence);
    }

    [Fact]
    public void ALabelAfterAHyphen_IsALabel()
    {
        var candidate = Assert.Single(UsMbiScanner.Detect("ID-MBI: 1A23C45DE67").Candidates);

        Assert.Equal(UsIdEvidence.Structure | UsIdEvidence.Context, candidate.Evidence);
    }

    [Fact]
    public void OtherWordsBetweenLabelAndValue_AreNotSkipped_ButTheStructureStillMatches()
    {
        var result = UsMbiScanner.Detect("MBI: ID 1A23C45DE67");

        Assert.Equal("S|8:11|Compact|1A23C45DE67", Describe(Assert.Single(result.Candidates)));
    }

    [Fact]
    public void CallerSuppliedStrongLabels_ReplaceTheDefault()
    {
        const string raw = "Medicare ID: 1A23C45DE67";

        var custom = UsMbiScanner.Detect(raw, ["Medicare ID"], []);
        var none = UsMbiScanner.Detect("MBI: 1A23C45DE67", [], []);

        Assert.Equal(UsIdEvidence.Structure | UsIdEvidence.Context, Assert.Single(custom.Candidates).Evidence);
        Assert.Equal(UsIdEvidence.Structure, Assert.Single(none.Candidates).Evidence);
    }

    [Fact]
    public void LogicalFieldLength_FollowsTheSharedFieldBinder_96Accepted_97Rejected()
    {
        string at96 = "MBI: " + Valid + new string(' ', 80);
        string at97 = "MBI: " + Valid + new string(' ', 81);

        Assert.Equal(96, at96.Length);
        Assert.Equal(UsIdEvidence.Structure | UsIdEvidence.Context, Assert.Single(UsMbiScanner.Detect(at96).Candidates).Evidence);
        var rejected = UsMbiScanner.Detect(at97);
        Assert.Equal(UsIdEvidence.Structure, Assert.Single(rejected.Candidates).Evidence);
        Assert.Contains(rejected.Rejections, r => r.Reason == UsMbiRejectionReason.FieldBindingRejected);
    }

    [Fact]
    public void TrailingSpacesBeforeATerminator_AreNotPartOfTheValueSpan()
    {
        var candidate = Assert.Single(UsMbiScanner.Detect("MBI: 1A23 C45 DE67   \nnext").Candidates);

        Assert.Equal("S+C|5:13|Spaced|1A23C45DE67", Describe(candidate));
    }

    // ---- inputs, results, diagnostics -----------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("MBI:")]
    [InlineData("MBI: ")]
    [InlineData("no identifiers here")]
    public void EmptyAndIdentifierFreeInputs_ProduceNothing(string? raw)
    {
        var result = UsMbiScanner.Detect(raw);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void NullLabelArguments_AreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => UsMbiScanner.Detect("x", null!, []));
        Assert.Throws<ArgumentNullException>(() => UsMbiScanner.Detect("x", [], null!));
    }

    [Fact]
    public void Detection_IsDeterministic()
    {
        string text = $"MBI: {Valid}\n{Valid2} 1B23C45DE67 MBI: 0A23C45DE67";

        var first = UsMbiScanner.Detect(text);
        var second = UsMbiScanner.Detect(text);

        Assert.Equal(first.Candidates.Select(Describe), second.Candidates.Select(Describe));
        Assert.Equal(first.Rejections.Select(r => r.ToString()), second.Rejections.Select(r => r.ToString()));
    }

    [Fact]
    public void ResultLists_AreReadOnly()
    {
        var result = UsMbiScanner.Detect("MBI: " + Valid);

        Assert.True(((System.Collections.IList)result.Candidates).IsReadOnly);
        Assert.True(((System.Collections.IList)result.Rejections).IsReadOnly);
        Assert.True(((System.Collections.IList)UsMbiResult.Empty.Candidates).IsReadOnly);
    }

    [Fact]
    public void DiagnosticOutput_NeverContainsTheIdentifier_OrAnyPartOfIt()
    {
        string raw = $"MBI: {Valid} and MBI: 1B23C45DE67 and {Valid2}";
        var result = UsMbiScanner.Detect(raw);

        var outputs = new List<string> { result.ToString() };
        outputs.AddRange(result.Candidates.Select(c => c.ToString()));
        outputs.AddRange(result.Candidates.Select(c => c.Canonical.ToString()));
        outputs.AddRange(result.Rejections.Select(r => r.ToString()));

        foreach (string output in outputs)
        {
            foreach (string fragment in new[] { "1A23", "C45", "DE67", "9Y99", "Y99Y", "YY99", "1B23" })
            {
                Assert.DoesNotContain(fragment, output);
            }
        }
    }

    // ---- scale ----------------------------------------------------------------------------------------------------

    [Fact]
    public void VeryLongInputs_StayNearLinear()
    {
        string digits = new('1', 2_000_000);
        string repeated = string.Concat(Enumerable.Repeat("1A23C45DE6 ", 200_000));
        string manyValid = string.Concat(Enumerable.Repeat(Valid + " ", 100_000));
        string labels = string.Concat(Enumerable.Repeat("MBI ", 50_000)) + "MBI: " + Valid;

        var sw = Stopwatch.StartNew();
        Assert.Empty(UsMbiScanner.Detect(digits).Candidates);
        Assert.Empty(UsMbiScanner.Detect(repeated).Candidates);
        Assert.Equal(100_000, UsMbiScanner.Detect(manyValid).Candidates.Count);
        var flood = UsMbiScanner.Detect(labels);
        sw.Stop();

        // The last field is still found after 50,000 adjacent labels (no restart cap, no silent drop).
        Assert.Contains(flood.Candidates, c => c.Evidence == (UsIdEvidence.Structure | UsIdEvidence.Context));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
    }
}
