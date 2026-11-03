using Xunit;

namespace Privon.Detection.Tests;

/// <summary>
/// D2.2 ends a logical field at "the start of another recognized field label". A label spelled INSIDE a letter/digit run of
/// the current value ("HO-TEL-1234", "1234-SSN-5678" without the hyphens) does not start a field: it is a substring of one
/// token. These tests pin that reading of "start of another label" for the TERMINATOR only -- a label must begin at an ASCII
/// token boundary to end the previous field -- and pin what the reading does NOT change: the initial label match stays
/// grammar-free (see FieldBinderContractTests.Char_LabelInsideAWordStillBinds...), a label directly followed by a value (zero
/// spaces, no separator) is still a label, and every other D2.2 rule.
///
/// "[DEFECT]" tests failed before the change; "[KEPT]" tests passed before it and must keep passing. SYNTHETIC text only.
/// </summary>
public sealed class FieldBinderTokenBoundaryTests
{
    private static IReadOnlyList<FieldBinding> Bind(string raw, params string[] labels)
    {
        var list = new List<FieldLabel>();
        foreach (var text in labels)
        {
            Assert.True(FieldLabel.TryCreate(text, out var label));
            list.Add(label);
        }

        return FieldBinder.Bind(NormalizedView.Build(raw), list);
    }

    private static string Slice(string raw, RawSpan span) => raw.Substring(span.Start, span.Length);

    // ---- [DEFECT] a label spelled inside an alphanumeric run does not end the value ----

    [Theory]
    [InlineData("Passport: HOTEL1234", "HOTEL1234")]        // Tel
    [InlineData("Passport: XDLN1234", "XDLN1234")]          // DLN
    [InlineData("Passport: 12MBI345", "12MBI345")]          // MBI
    [InlineData("Passport: 1234SSN5678", "1234SSN5678")]    // SSN
    [InlineData("Passport: ZPHONE12", "ZPHONE12")]          // Phone, mid-token
    [InlineData("Passport: 9STATE99", "9STATE99")]          // State, mid-token
    public void Defect_ALabelInsideAnAlphanumericRun_DoesNotEndTheValue(string raw, string value)
    {
        var binding = Assert.Single(Bind(raw, "Passport", "Tel", "DLN", "MBI", "SSN", "Phone", "State"));

        Assert.Equal(FieldBindingState.FieldAccepted, binding.State);
        Assert.Equal(FieldBindingTerminator.EndOfText, binding.Terminator);
        Assert.Equal(value, Slice(raw, binding.CandidateSpan));
    }

    [Fact]
    public void Defect_TheSameTokenFollowedByARealLabel_EndsAtTheRealLabelOnly()
    {
        const string raw = "Passport: HOTEL1234 SSN: 321-54-9876";

        var bindings = Bind(raw, "Passport", "Tel", "SSN");

        Assert.Equal(2, bindings.Count);
        Assert.Equal("HOTEL1234 ", Slice(raw, bindings[0].CandidateSpan));
        Assert.Equal(FieldBindingTerminator.NextRecognizedLabel, bindings[0].Terminator);
        Assert.Equal("SSN", bindings[1].Label.Value);
        Assert.Equal("321-54-9876", Slice(raw, bindings[1].CandidateSpan));
    }

    [Fact]
    public void Defect_ALabelInsideATokenAtTheFirstValuePosition_WithoutAnExplicitSeparator_DoesNotMakeTheFieldEmpty()
    {
        // "Passport" is directly followed by letters of the same run and "SSN" is not followed by ':' '#' '=': not a field start.
        // (The earlier version of this test used "PassportSSN: 1"; that glued form is now governed by the Glued_ tests.)
        const string raw = "PassportSSN 1";

        var binding = Assert.Single(Bind(raw, "Passport", "SSN"));

        Assert.Equal("SSN 1", Slice(raw, binding.CandidateSpan));
    }

    // ---- GLUED labels (synthetic implementation assumption, NOT a D2.2 clause): a label that starts inside a token still
    //      ends the field when an explicit ':' '#' '=' follows it immediately ----

    [Theory]
    [InlineData("Passport: A12345678Tel: 555", "A12345678", "555")]
    [InlineData("Passport: A12345678Tel#555", "A12345678", "555")]
    [InlineData("Passport: A12345678Tel=555", "A12345678", "555")]
    [InlineData("Passport: ZX81K2State: NY", "ZX81K2", "NY")]
    [InlineData("Passport: A12345678TEL:555", "A12345678", "555")]
    public void Glued_AnExplicitSeparatorAfterTheLabel_EndsTheFieldAtTheLabelStart(string raw, string first, string second)
    {
        var bindings = Bind(raw, "Passport", "Tel", "State");

        Assert.Equal(2, bindings.Count);
        Assert.Equal(first, Slice(raw, bindings[0].CandidateSpan));
        Assert.Equal(FieldBindingTerminator.NextRecognizedLabel, bindings[0].Terminator);
        Assert.Equal(second, Slice(raw, bindings[1].CandidateSpan));
        Assert.True(bindings[0].LogicalFieldSpan.End <= bindings[1].LabelSpan.Start); // no overlap, no absorption
    }

    [Theory]
    [InlineData("Passport: A12345678Tel 555")]        // no separator: still one token
    [InlineData("Passport: A12345678Tel555")]
    [InlineData("Passport: A12345678Tel : 555")]      // a space before the separator is not "immediately after the label"
    [InlineData("Passport: A12345678Tel-555")]        // '-' is not a separator
    [InlineData("Passport: HOTEL1234")]
    public void Glued_WithoutAnImmediateExplicitSeparator_TheLabelSpellingStaysInsideTheValue(string raw)
    {
        var binding = Assert.Single(Bind(raw, "Passport", "Tel"));

        Assert.Equal(raw["Passport: ".Length..], Slice(raw, binding.CandidateSpan));
        Assert.Equal(FieldBindingTerminator.EndOfText, binding.Terminator);
    }

    [Fact]
    public void Glued_ALabelThatItselfEndsWithASeparator_CountsAsExplicit()
    {
        // "ssn=" carries its own '='; "followed by" is read as satisfied when the label's last character is the separator.
        const string raw = "Passport: A12345678ssn=321549876";

        var bindings = Bind(raw, "Passport", "ssn=");

        Assert.Equal(2, bindings.Count);
        Assert.Equal("A12345678", Slice(raw, bindings[0].CandidateSpan));
        Assert.Equal("321549876", Slice(raw, bindings[1].CandidateSpan));
    }

    [Fact]
    public void Glued_TheLongestEligibleLabelWins_AndAnIneligibleOneNeverEndsAField()
    {
        // "Telephone" and "Tel" both start here; "Telephone" is followed by ':' and is the longer: it wins.
        var bindings = Bind("Passport: A12345678Telephone: 5", "Passport", "Tel", "Telephone");
        Assert.Equal("Telephone", bindings[1].Label.Value);

        // Neither is followed by a separator: no field ends here.
        Assert.Single(Bind("Passport: A12345678Telephone 5", "Passport", "Tel", "Telephone"));

        // Without "Telephone" in the list, "Tel" is followed by 'e', not a separator: no field ends here.
        Assert.Single(Bind("Passport: A12345678Telephone: 5", "Passport", "Tel"));
    }

    [Fact]
    public void Glued_TheFirstLabelMatchAndTheRightEdgeAreUnchanged()
    {
        Assert.Equal(new RawSpan(4, 4), Assert.Single(Bind("Username: x", "Name")).LabelSpan);

        const string raw = "Passport: A12345678 SSN321549876";
        var bindings = Bind(raw, "Passport", "SSN");
        Assert.Equal(2, bindings.Count);
        Assert.Equal("321549876", Slice(raw, bindings[1].CandidateSpan));
    }

    [Fact]
    public void Glued_SpansAfterSurrogatePairsAreExact_AndRepeatedGluedLabelsAreBounded()
    {
        const string prefix = "\U0001F600\U0001F4A9 ";
        string raw = prefix + "Passport: A12345678Tel: 555";
        var bindings = Bind(raw, "Passport", "Tel");
        Assert.Equal(new RawSpan(prefix.Length + 10, 9), bindings[0].CandidateSpan);
        Assert.Equal(new RawSpan(prefix.Length + 24, 3), bindings[1].CandidateSpan);

        string many = string.Concat(Enumerable.Repeat("Passport: A12345678Tel: 555 ", 20_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var found = Bind(many, "Passport", "Tel");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(40_000, found.Count);
    }

    // ---- [KEPT] a real next field still ends the current one ----

    [Theory]
    [InlineData("Passport: A12345678 SSN: 321-54-9876", "A12345678 ")]
    [InlineData("Passport: A12345678;SSN: 321-54-9876", "A12345678")]
    [InlineData("Passport: A12345678\tSSN: 321-54-9876", "A12345678")]
    [InlineData("Passport: A12345678|SSN: 321-54-9876", "A12345678")]
    [InlineData("Passport: A12345678\rSSN: 321-54-9876", "A12345678")]
    [InlineData("Passport: A12345678\nSSN: 321-54-9876", "A12345678")]
    [InlineData("Passport: A12345678\r\nSSN: 321-54-9876", "A12345678")]
    [InlineData("Passport: A12345678-SSN: 321-54-9876", "A12345678-")]            // a hyphen is not an alphanumeric: SSN starts a token
    [InlineData("Passport: A12345678한SSN: 321-54-9876", "A12345678한")]  // a non-ASCII letter is a boundary (ASCII rule)
    public void Kept_ARealNextLabel_EndsTheValue_AndBindsItsOwn(string raw, string firstValue)
    {
        var bindings = Bind(raw, "Passport", "SSN", "Tel");

        Assert.Equal(2, bindings.Count);
        Assert.Equal(firstValue, Slice(raw, bindings[0].CandidateSpan));
        Assert.Equal("SSN", bindings[1].Label.Value);
        Assert.Equal("321-54-9876", Slice(raw, bindings[1].CandidateSpan));
    }

    [Fact]
    public void Kept_ALabelDirectlyFollowedByItsValue_IsStillALabel_BecauseD22AllowsZeroSpacesAndNoSeparator()
    {
        // The right-hand side is NOT a boundary condition: "SSN321549876" is the label SSN with an immediately bound value.
        const string raw = "Passport: A12345678 SSN321549876";

        var bindings = Bind(raw, "Passport", "SSN");

        Assert.Equal(2, bindings.Count);
        Assert.Equal("A12345678 ", Slice(raw, bindings[0].CandidateSpan));
        Assert.Equal("321549876", Slice(raw, bindings[1].CandidateSpan));
    }

    [Fact]
    public void Kept_AValueThatBeginsWithALabelSpelling_IsStillAMarkerAtTheValueStart()
    {
        // Ambiguity D2.2 does not resolve ("Tel1234": the label Tel plus 1234, or one token?). The pinned behavior is
        // unchanged: a label exactly at the value start ends the field.
        const string raw = "Passport: Tel1234";

        var bindings = Bind(raw, "Passport", "Tel");

        Assert.Equal(FieldBindingState.RejectedEmptyCandidate, bindings[0].State);
        Assert.Equal("1234", Slice(raw, bindings[1].CandidateSpan));
    }

    [Fact]
    public void Kept_TheInitialLabelMatchIsStillGrammarFree()
    {
        const string raw = "Username: x";

        var binding = Assert.Single(Bind(raw, "Name"));

        Assert.Equal(new RawSpan(4, 4), binding.LabelSpan);
    }

    [Fact]
    public void Kept_ALabelWithANonAlphanumericEdgeNeedsNoBoundaryOnThatEdge()
    {
        // "ssn=" ends with '=', "ITIN #" starts with a letter: only an ASCII-alphanumeric FIRST character is boundary-checked.
        const string raw = "Passport: A12345678 ssn=321549876";

        var bindings = Bind(raw, "Passport", "ssn=");

        Assert.Equal(2, bindings.Count);
        Assert.Equal("321549876", Slice(raw, bindings[1].CandidateSpan));
    }

    [Fact]
    public void Kept_ALabelThatBeginsWithANonAlphanumeric_NeedsNoBoundaryBeforeIt()
    {
        // Only a label whose FIRST character is an ASCII letter or digit can be "inside a token".
        const string raw = "Passport: AB#ID: 5";

        var bindings = Bind(raw, "Passport", "#ID");

        Assert.Equal(2, bindings.Count);
        Assert.Equal("AB", Slice(raw, bindings[0].CandidateSpan));
        Assert.Equal("#ID", bindings[1].Label.Value);
    }

    [Fact]
    public void Kept_TheLongestLabelStillWins_AtTheTerminator()
    {
        const string raw = "A: x Social Security Number: 1";

        var bindings = Bind(raw, "A", "Social", "Social Security Number");

        Assert.Equal("Social Security Number", bindings[1].Label.Value);
    }

    [Fact]
    public void Kept_TheNinetySixCharacterLimitAndOffsetsAfterSurrogatePairs_AreUnchanged()
    {
        string raw = "\U0001F600\U0001F4A9 Passport: HOTEL1234" + new string(' ', 96 - 19);

        var binding = Assert.Single(Bind(raw, "Passport"));

        Assert.Equal(96, binding.LogicalFieldSpan.Length);
        Assert.Equal(FieldBindingState.FieldAccepted, binding.State);
        Assert.Equal(5, binding.LogicalFieldSpan.Start);
        Assert.Equal(FieldBindingState.FieldRejectedOverMaxLength, Assert.Single(Bind(raw + " ", "Passport")).State);
    }

    [Fact]
    public void Bounded_ManyRepeatedLabelsAndLongRuns_StayNearLinear()
    {
        string labels = string.Concat(Enumerable.Repeat("Passport: HOTEL1234 ", 20_000));
        string run = "Passport: " + new string('x', 300_000) + "TEL" + new string('y', 300_000);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var many = Bind(labels, "Passport", "Tel", "SSN");
        var longRun = Assert.Single(Bind(run, "Passport", "Tel", "SSN"));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "binding must stay near-linear");
        Assert.Equal(20_000, many.Count);
        Assert.All(many, b => Assert.Equal("HOTEL1234 ", Slice(labels, b.CandidateSpan)));
        Assert.Equal(FieldBindingState.FieldRejectedOverMaxLength, longRun.State);
        Assert.Equal(FieldBindingTerminator.EndOfText, longRun.Terminator);
    }
}
