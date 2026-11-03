using System.Diagnostics;
using System.Text.RegularExpressions;
using Privon.UsMbiSevis;

namespace Privon.UsMbiSevis.Tests;

// SYNTHETIC DATA ONLY. Behaviour beyond the frozen D3A corpus. Where the contract (D2.2) is silent the expectation is
// marked [EA] (experiment assumption); such tests document what this experiment does, not what the official rule is.
public class UsSevisBehaviorTests
{
    private const string Valid = "N1234567890";   // synthetic
    private const string Valid2 = "N0987654321";   // synthetic

    private static string Describe(UsSevisCandidate c) =>
        $"{(c.Evidence == (UsIdEvidence.Structure | UsIdEvidence.Context) ? "S+C" : c.Evidence == UsIdEvidence.Structure ? "S" : "?")}"
        + $"|{c.Span.Start}:{c.Span.Length}|{c.Canonical.GetValueForTestingOnly()}";

    // ---- exact shape ----------------------------------------------------------------------------------------------

    [Fact]
    public void ExactShape_AgreesWithAnIndependentContractRegex_ForEveryTenDigitSuffixSample()
    {
        var contract = new Regex("^N[0-9]{10}$", RegexOptions.CultureInvariant);
        var mismatches = new List<string>();
        var rng = new Random(20260501); // fixed seed: deterministic synthetic samples

        for (int i = 0; i < 2000; i++)
        {
            var chars = new char[11];
            chars[0] = "NNNNMnX1"[rng.Next(8)];
            for (int k = 1; k < 11; k++)
            {
                chars[k] = (char)('0' + rng.Next(10));
            }

            if (rng.Next(5) == 0)
            {
                chars[rng.Next(11)] = "aN9Z-"[rng.Next(5)];
            }

            string s = new(chars);
            int expected = contract.IsMatch(s) ? 1 : 0;
            if (UsSevisScanner.Detect(s).Candidates.Count != expected
                || UsSevisScanner.Detect("ref " + s + " end").Candidates.Count != expected
                || UsSevisScanner.Detect("SEVIS: " + s).Candidates.Count != expected)
            {
                mismatches.Add($"sample{i}");
            }
        }

        Assert.Empty(mismatches);
    }

    [Theory]
    [InlineData("n1234567890")]          // lower-case n: the contract says exact ASCII N
    [InlineData("M1234567890")]
    [InlineData("A1234567890")]
    [InlineData("Ｎ1234567890")]     // fullwidth N
    [InlineData("Н1234567890")]     // Cyrillic En (looks like N)
    [InlineData("N123456789٠")]     // Arabic-Indic zero
    [InlineData("N12345678９０") ] // fullwidth digits
    [InlineData("N 1234567890")]
    [InlineData("N-1234567890")]
    [InlineData("N123-456-7890")]
    [InlineData("N.1234567890")]
    [InlineData("N12345678901")]
    [InlineData("N123456789")]
    [InlineData("N12345678")]
    [InlineData("N")]
    [InlineData("1234567890")]
    [InlineData("N123456789012345")]
    public void NearMisses_ProduceNoCandidate(string raw)
    {
        Assert.Empty(UsSevisScanner.Detect(raw).Candidates);
        Assert.Empty(UsSevisScanner.Detect("SEVIS: " + raw).Candidates);
    }

    [Theory]
    [InlineData("n1234567890", UsSevisRejectionReason.PrefixMustBeN)]
    [InlineData("A1234567890", UsSevisRejectionReason.PrefixMustBeN)]
    [InlineData("Ｎ1234567890", UsSevisRejectionReason.NonAsciiLookalike)]
    [InlineData("Н1234567890", UsSevisRejectionReason.NonAsciiLookalike)]
    [InlineData("N123456789٠", UsSevisRejectionReason.NonAsciiDigit)]
    [InlineData("N12345678９０", UsSevisRejectionReason.NonAsciiDigit)]
    [InlineData("N12345678901", UsSevisRejectionReason.WrongLength)]
    [InlineData("N123456789012345", UsSevisRejectionReason.WrongLength)]
    [InlineData("N123456789", UsSevisRejectionReason.WrongLength)]
    [InlineData("A12345678901", UsSevisRejectionReason.WrongLength)]
    public void NearMisses_ReportTheSpecificReason(string raw, UsSevisRejectionReason reason)
    {
        var result = UsSevisScanner.Detect(raw);

        Assert.Contains(result.Rejections, r => r.Reason == reason);
    }

    [Fact]
    public void WrongLengthIsReportedBeforeAPrefixOrDigitProblem_EaOrdering()
    {
        var result = UsSevisScanner.Detect("M12345678901");

        Assert.Contains(result.Rejections, r => r.Reason == UsSevisRejectionReason.WrongLength);
        Assert.DoesNotContain(result.Rejections, r => r.Reason == UsSevisRejectionReason.PrefixMustBeN);
    }

    // ---- boundaries -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", "")]
    [InlineData(" ", " ")]
    [InlineData("(", ")")]
    [InlineData("[", "]")]
    [InlineData("\"", "\"")]
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
        var candidate = Assert.Single(UsSevisScanner.Detect(before + Valid + after).Candidates);

        Assert.Equal(before.Length, candidate.Span.Start);
        Assert.Equal(11, candidate.Span.Length);
    }

    [Theory]
    [InlineData("X", "")]
    [InlineData("x", "")]
    [InlineData("7", "")]
    [InlineData("é", "")]
    [InlineData("А", "")]
    [InlineData("１", "")]
    [InlineData("٣", "")]
    [InlineData("", "X")]
    [InlineData("", "x")]
    [InlineData("", "9")]
    [InlineData("", "é")]
    [InlineData("", "А")]
    [InlineData("", "１")]
    [InlineData("", "٣")]
    [InlineData("X", "Y")]
    [InlineData("N", "")]
    public void AlphanumericNeighbours_AreBoundaryViolations_IncludingNonAsciiLookalikes(string before, string after)
    {
        var result = UsSevisScanner.Detect(before + Valid + after);

        Assert.Empty(result.Candidates);
        Assert.NotEmpty(result.Rejections);
    }

    [Fact]
    public void EmbeddedCore_IsReportedAsABoundaryViolation_NotAsAWrongLength()
    {
        Assert.Contains(UsSevisScanner.Detect("XN1234567890Y").Rejections, r => r.Reason == UsSevisRejectionReason.BoundaryViolation);
        Assert.Contains(UsSevisScanner.Detect("N1234567890X").Rejections, r => r.Reason == UsSevisRejectionReason.BoundaryViolation);
        Assert.Contains(UsSevisScanner.Detect("9N1234567890").Rejections, r => r.Reason == UsSevisRejectionReason.BoundaryViolation);
    }

    [Fact]
    public void SeveralSevisIds_AreAllReportedInOrder_IncludingIdenticalValues()
    {
        string text = $"a {Valid}, b {Valid2}; c {Valid}";

        var result = UsSevisScanner.Detect(text);

        Assert.Equal(
            new[] { "S|2:11|N1234567890", "S|17:11|N0987654321", "S|32:11|N1234567890" },
            result.Candidates.Select(Describe));
    }

    // ---- labels and field binding ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("SEVIS: N1234567890", 7)]
    [InlineData("SEVIS ID: N1234567890", 10)]
    [InlineData("SEVIS Number: N1234567890", 14)]
    [InlineData("sevis id: N1234567890", 10)]
    [InlineData("SEVIS ID N1234567890", 9)]
    [InlineData("SEVIS#N1234567890", 6)]
    [InlineData("SEVIS Number = N1234567890", 15)]
    [InlineData("SEVIS   :   N1234567890", 12)]
    public void LabelledValue_GetsStructureAndContext_AtTheExactValueSpan(string raw, int valueStart)
    {
        var candidate = Assert.Single(UsSevisScanner.Detect(raw).Candidates);

        Assert.Equal($"S+C|{valueStart}:11|N1234567890", Describe(candidate));
    }

    [Theory]
    [InlineData("SEVIS    : N1234567890")]
    [InlineData("SEVIS:     N1234567890")]
    public void WhenTheFieldBindingIsRejected_TheValueIsStillASevisByStructureAlone(string raw)
    {
        var result = UsSevisScanner.Detect(raw);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(UsIdEvidence.Structure, candidate.Evidence); // the contract makes an exact SEVIS shape Level 3
        Assert.Contains(result.Rejections, r => r.Reason == UsSevisRejectionReason.FieldBindingRejected);
    }

    [Theory]
    [InlineData('\r')]
    [InlineData('\n')]
    [InlineData(';')]
    [InlineData('\t')]
    [InlineData('|')]
    public void FieldTerminators_EndTheValue(char terminator)
    {
        var candidate = Assert.Single(UsSevisScanner.Detect($"SEVIS: {Valid}{terminator}memo").Candidates);

        Assert.Equal("S+C|7:11|N1234567890", Describe(candidate));
    }

    [Fact]
    public void NextRecognizedLabel_IsUsedOnlyWhenTheCallerSuppliesIt()
    {
        const string raw = "SEVIS: N1234567890 Phone: 2125551212";

        var with = UsSevisScanner.Detect(raw, UsSevisScanner.DefaultStrongLabels, ["Phone"]);
        var without = UsSevisScanner.Detect(raw);

        Assert.Equal("S+C|7:11|N1234567890", Describe(Assert.Single(with.Candidates)));
        Assert.Empty(with.Rejections);
        // [EA] the field then runs on past the value: context is not credited, but the structure alone still matches.
        Assert.Equal("S|7:11|N1234567890", Describe(Assert.Single(without.Candidates)));
        Assert.Contains(without.Rejections, r => r.Reason == UsSevisRejectionReason.TrailingContent);
    }

    [Theory]
    [InlineData("XSEVIS: N1234567890")]
    [InlineData("SEVISX: N1234567890")]
    [InlineData("9SEVIS ID: N1234567890")]
    public void ALabelInsideAWord_IsNotALabel_EA(string raw)
    {
        Assert.Equal(UsIdEvidence.Structure, Assert.Single(UsSevisScanner.Detect(raw).Candidates).Evidence);
    }

    [Fact]
    public void TheLongestLabelWins_SoSevisIdIsNotReadAsSevisFollowedByAnId()
    {
        var candidate = Assert.Single(UsSevisScanner.Detect("SEVIS ID: N1234567890").Candidates);

        Assert.Equal(UsIdEvidence.Structure | UsIdEvidence.Context, candidate.Evidence);
    }

    [Fact]
    public void CallerSuppliedStrongLabels_ReplaceTheDefault()
    {
        var custom = UsSevisScanner.Detect("Student ID: N1234567890", ["Student ID"], []);
        var none = UsSevisScanner.Detect("SEVIS: N1234567890", [], []);

        Assert.Equal(UsIdEvidence.Structure | UsIdEvidence.Context, Assert.Single(custom.Candidates).Evidence);
        Assert.Equal(UsIdEvidence.Structure, Assert.Single(none.Candidates).Evidence);
    }

    [Fact]
    public void LogicalFieldLength_FollowsTheSharedFieldBinder_96Accepted_97Rejected()
    {
        string at96 = "SEVIS: " + Valid + new string(' ', 78);
        string at97 = "SEVIS: " + Valid + new string(' ', 79);

        Assert.Equal(96, at96.Length);
        Assert.Equal(UsIdEvidence.Structure | UsIdEvidence.Context, Assert.Single(UsSevisScanner.Detect(at96).Candidates).Evidence);
        var rejected = UsSevisScanner.Detect(at97);
        Assert.Equal(UsIdEvidence.Structure, Assert.Single(rejected.Candidates).Evidence);
        Assert.Contains(rejected.Rejections, r => r.Reason == UsSevisRejectionReason.FieldBindingRejected);
    }

    [Fact]
    public void TwoIdsAfterOneLabel_AreBothSevisByStructure_ButOnlyTheFieldValueRunsOn()
    {
        var result = UsSevisScanner.Detect($"SEVIS: {Valid} {Valid2}");

        Assert.Equal(new[] { "S|7:11|N1234567890", "S|19:11|N0987654321" }, result.Candidates.Select(Describe));
        Assert.Contains(result.Rejections, r => r.Reason == UsSevisRejectionReason.TrailingContent);
    }

    // ---- inputs, results, diagnostics -----------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SEVIS:")]
    [InlineData("SEVIS ID: ")]
    [InlineData("no identifiers here")]
    public void EmptyAndIdentifierFreeInputs_ProduceNothing(string? raw)
    {
        Assert.Empty(UsSevisScanner.Detect(raw).Candidates);
    }

    [Fact]
    public void NullLabelArguments_AreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => UsSevisScanner.Detect("x", null!, []));
        Assert.Throws<ArgumentNullException>(() => UsSevisScanner.Detect("x", [], null!));
    }

    [Fact]
    public void Detection_IsDeterministic()
    {
        string text = $"SEVIS: {Valid}\n{Valid2} M1234567890 SEVIS: N12345";

        var first = UsSevisScanner.Detect(text);
        var second = UsSevisScanner.Detect(text);

        Assert.Equal(first.Candidates.Select(Describe), second.Candidates.Select(Describe));
        Assert.Equal(first.Rejections.Select(r => r.ToString()), second.Rejections.Select(r => r.ToString()));
    }

    [Fact]
    public void ResultLists_AreReadOnly()
    {
        var result = UsSevisScanner.Detect("SEVIS: " + Valid);

        Assert.True(((System.Collections.IList)result.Candidates).IsReadOnly);
        Assert.True(((System.Collections.IList)result.Rejections).IsReadOnly);
        Assert.True(((System.Collections.IList)UsSevisResult.Empty.Candidates).IsReadOnly);
    }

    [Fact]
    public void DiagnosticOutput_NeverContainsTheIdentifier_OrAnyPartOfIt()
    {
        string raw = $"SEVIS: {Valid} and M1234567890 and {Valid2}";
        var result = UsSevisScanner.Detect(raw);

        var outputs = new List<string> { result.ToString() };
        outputs.AddRange(result.Candidates.Select(c => c.ToString()));
        outputs.AddRange(result.Candidates.Select(c => c.Canonical.ToString()));
        outputs.AddRange(result.Rejections.Select(r => r.ToString()));

        foreach (string output in outputs)
        {
            foreach (string fragment in new[] { "1234", "5678", "7890", "0987", "4321", "M123" })
            {
                Assert.DoesNotContain(fragment, output);
            }
        }
    }

    // ---- scale ----------------------------------------------------------------------------------------------------

    [Fact]
    public void VeryLongInputs_StayNearLinear()
    {
        string digits = "N" + new string('1', 2_000_000);
        string letters = new('N', 2_000_000);
        string manyValid = string.Concat(Enumerable.Repeat(Valid + " ", 100_000));
        string labels = string.Concat(Enumerable.Repeat("SEVIS ", 50_000)) + "SEVIS: " + Valid;

        var sw = Stopwatch.StartNew();
        Assert.Empty(UsSevisScanner.Detect(digits).Candidates);
        Assert.Empty(UsSevisScanner.Detect(letters).Candidates);
        Assert.Equal(100_000, UsSevisScanner.Detect(manyValid).Candidates.Count);
        var flood = UsSevisScanner.Detect(labels);
        sw.Stop();

        // The last field is still found after 50,000 adjacent labels (no restart cap, no silent drop).
        Assert.Contains(flood.Candidates, c => c.Evidence == (UsIdEvidence.Structure | UsIdEvidence.Context));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
    }
}
