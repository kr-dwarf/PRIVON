using Xunit;

namespace Privon.Detection.Tests;

/// <summary>
/// The shared field-binding primitive against the D2.2 binding grammar (Notion "040-US-D2.2 deterministic field and record
/// grammar"): a logical field is one recognized label plus its single bound value; after the label 0-3 spaces, then none or
/// exactly one of ':' '#' '=', then 0-3 spaces; the value starts immediately; binding terminates at the EARLIEST of CR, LF,
/// ';', HT, '|' or the START of another recognized label; at most 96 raw characters from label start through candidate end.
///
/// SYNTHETIC text only. FieldBinder stays grammar-free: the recognized-label set is always an input.
/// "[CHAR]" tests pin existing behavior that must not change; "[DEFECT]" tests pin the D2.2 rule that was not honored.
/// </summary>
public sealed class FieldBinderContractTests
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

    // ---- [DEFECT] a recognized label exactly at the value start ends the field (the field is empty) ----

    [Fact]
    public void Defect_ItinThenPhone_ItinFieldIsEmpty_PhoneBindsItsOwnValue()
    {
        const string raw = "ITIN: Phone: 912-70-1234";

        var bindings = Bind(raw, "ITIN", "Phone");

        Assert.Equal(2, bindings.Count);
        Assert.Equal("ITIN", bindings[0].Label.Value);
        Assert.Equal(FieldBindingState.RejectedEmptyCandidate, bindings[0].State);
        Assert.Equal(FieldBindingTerminator.NextRecognizedLabel, bindings[0].Terminator);
        Assert.Equal(0, bindings[0].CandidateSpan.Length);
        Assert.Equal("Phone", bindings[1].Label.Value);
        Assert.Equal(FieldBindingState.FieldAccepted, bindings[1].State);
        Assert.Equal("912-70-1234", Slice(raw, bindings[1].CandidateSpan));
    }

    [Fact]
    public void Defect_PhoneThenItin_PhoneFieldIsEmpty_ItinBindsItsOwnValue()
    {
        const string raw = "Phone: ITIN: 912-70-1234";

        var bindings = Bind(raw, "ITIN", "Phone");

        Assert.Equal(2, bindings.Count);
        Assert.Equal(FieldBindingState.RejectedEmptyCandidate, bindings[0].State);
        Assert.Equal("ITIN", bindings[1].Label.Value);
        Assert.Equal("912-70-1234", Slice(raw, bindings[1].CandidateSpan));
    }

    public static IEnumerable<object[]> LabelPairs()
    {
        var labels = new[] { "ITIN", "ATIN", "SSN", "Phone", "Name", "I-94", "Social Security Number", "ZZTOP" };
        foreach (var first in labels)
        foreach (var second in labels)
        {
            if (first != second) yield return [first, second];
        }
    }

    [Theory]
    [MemberData(nameof(LabelPairs))]
    public void Defect_AnyLabelDirectlyBeforeAnyOther_EndsTheFirstField(string first, string second)
    {
        string raw = $"{first}: {second}: value-1";

        var bindings = Bind(raw, first, second);

        Assert.Equal(2, bindings.Count);
        Assert.Equal(first, bindings[0].Label.Value);
        Assert.Equal(FieldBindingState.RejectedEmptyCandidate, bindings[0].State);
        Assert.Equal(FieldBindingTerminator.NextRecognizedLabel, bindings[0].Terminator);
        Assert.Equal(second, bindings[1].Label.Value);
        Assert.Equal("value-1", Slice(raw, bindings[1].CandidateSpan));
    }

    [Fact]
    public void Defect_ChainOfLabels_EachEmptyFieldEndsAtTheNextLabel()
    {
        const string raw = "A: B: C: x";

        var bindings = Bind(raw, "A", "B", "C");

        Assert.Equal(["A", "B", "C"], bindings.Select(b => b.Label.Value).ToArray());
        Assert.Equal(
            [FieldBindingState.RejectedEmptyCandidate, FieldBindingState.RejectedEmptyCandidate, FieldBindingState.FieldAccepted],
            bindings.Select(b => b.State).ToArray());
        Assert.Equal("x", Slice(raw, bindings[2].CandidateSpan));
    }

    [Theory]
    [InlineData("A  B: x")]    // spaces, no separator
    [InlineData("A#B=x")]      // different separators, no spaces
    [InlineData("A = B : x")]  // spaces around separators
    [InlineData("A   :   B: x")]
    public void Defect_LabelAtValueStartAfterAnySeparatorForm_EndsTheField(string raw)
    {
        var bindings = Bind(raw, "A", "B");

        Assert.Equal(2, bindings.Count);
        Assert.Equal(FieldBindingState.RejectedEmptyCandidate, bindings[0].State);
        Assert.Equal("B", bindings[1].Label.Value);
        Assert.Equal(FieldBindingState.FieldAccepted, bindings[1].State);
    }

    [Fact]
    public void Defect_TheSameLabelRepeated_FirstFieldIsEmpty()
    {
        const string raw = "A: A: x";

        var bindings = Bind(raw, "A");

        Assert.Equal(2, bindings.Count);
        Assert.Equal(FieldBindingState.RejectedEmptyCandidate, bindings[0].State);
        Assert.Equal("x", Slice(raw, bindings[1].CandidateSpan));
    }

    [Fact]
    public void Defect_ThousandsOfAdjacentLabels_AreBoundedAndAFollowingFieldStillBinds()
    {
        string flood = string.Concat(Enumerable.Repeat("Phone: ", 20_000));
        string raw = flood + "ITIN: 912-70-1234";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var bindings = Bind(raw, "Phone", "ITIN");

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "binding must stay near-linear");
        Assert.Equal(20_001, bindings.Count);
        var last = bindings[^1];
        Assert.Equal("ITIN", last.Label.Value);
        Assert.Equal("912-70-1234", Slice(raw, last.CandidateSpan));
    }

    // ---- [CHAR] behavior that must not change ----

    [Fact]
    public void Char_ValidLabelAndValue_ExactSpans()
    {
        const string raw = "SSN: 321549876";

        var binding = Assert.Single(Bind(raw, "SSN"));

        Assert.Equal(new RawSpan(0, 3), binding.LabelSpan);
        Assert.Equal(new RawSpan(5, 9), binding.CandidateSpan);
        Assert.Equal(new RawSpan(0, 14), binding.LogicalFieldSpan);
        Assert.Equal(':', binding.Separator);
        Assert.Equal(FieldBindingTerminator.EndOfText, binding.Terminator);
        Assert.Equal(FieldBindingState.FieldAccepted, binding.State);
    }

    [Theory]
    [InlineData("A: x\ry", FieldBindingTerminator.Cr)]
    [InlineData("A: x\ny", FieldBindingTerminator.Lf)]
    [InlineData("A: x;y", FieldBindingTerminator.Semicolon)]
    [InlineData("A: x\ty", FieldBindingTerminator.HorizontalTab)]
    [InlineData("A: x|y", FieldBindingTerminator.VerticalBar)]
    public void Char_TerminatorsEndTheValue(string raw, FieldBindingTerminator terminator)
    {
        var binding = Assert.Single(Bind(raw, "A"));

        Assert.Equal(terminator, binding.Terminator);
        Assert.Equal("x", Slice(raw, binding.CandidateSpan));
        Assert.Equal(FieldBindingState.FieldAccepted, binding.State);
    }

    [Fact]
    public void Char_ALabelInTheMiddleOfAValue_EndsTheValue()
    {
        const string raw = "A: x B: y";

        var bindings = Bind(raw, "A", "B");

        Assert.Equal(2, bindings.Count);
        Assert.Equal(FieldBindingTerminator.NextRecognizedLabel, bindings[0].Terminator);
        Assert.Equal("x ", Slice(raw, bindings[0].CandidateSpan));
        Assert.Equal("y", Slice(raw, bindings[1].CandidateSpan));
    }

    [Fact]
    public void Char_IndependentFieldsAreAllBound()
    {
        const string raw = "A: one;B: two|A: three\nB: four";

        var bindings = Bind(raw, "A", "B");

        Assert.Equal(["one", "two", "three", "four"], bindings.Select(b => Slice(raw, b.CandidateSpan)).ToArray());
        Assert.All(bindings, b => Assert.Equal(FieldBindingState.FieldAccepted, b.State));
    }

    [Fact]
    public void Char_NinetySixRawCharactersAreAccepted_NinetySevenAreRejected()
    {
        string at96 = "L:" + new string('A', 94);
        string at97 = "L:" + new string('A', 95);

        Assert.Equal(96, at96.Length);
        Assert.Equal(FieldBindingState.FieldAccepted, Assert.Single(Bind(at96, "L")).State);
        Assert.Equal(FieldBindingState.FieldRejectedOverMaxLength, Assert.Single(Bind(at97, "L")).State);
    }

    [Fact]
    public void Char_SeparatorAdjacentSpaces_ZeroToThreeAccepted_FourRejected()
    {
        Assert.Equal(FieldBindingState.FieldAccepted, Assert.Single(Bind("A   : x", "A")).State);
        Assert.Equal(FieldBindingState.RejectedPreSeparatorSpaces, Assert.Single(Bind("A    : x", "A")).State);
        Assert.Equal(FieldBindingState.FieldAccepted, Assert.Single(Bind("A:   x", "A")).State);
        Assert.Equal(FieldBindingState.RejectedPostSeparatorSpaces, Assert.Single(Bind("A:    x", "A")).State);
    }

    [Theory]
    [InlineData("A: x", ':')]
    [InlineData("A# x", '#')]
    [InlineData("A= x", '=')]
    public void Char_OnlyColonHashEqualsAreSeparators(string raw, char separator)
    {
        Assert.Equal(separator, Assert.Single(Bind(raw, "A")).Separator);
    }

    [Fact]
    public void Char_OtherPunctuationIsPartOfTheValue_NotASeparator()
    {
        const string raw = "A-x";

        var binding = Assert.Single(Bind(raw, "A"));

        Assert.Null(binding.Separator);
        Assert.Equal("-x", Slice(raw, binding.CandidateSpan));
    }

    [Fact]
    public void Char_LabelMatchIsAsciiCaseInsensitiveOnly()
    {
        Assert.Single(Bind("ssn: 1", "SSN"));
        Assert.Single(Bind("SsN: 1", "ssn"));
        Assert.Empty(Bind("\u0131d: 1", "ID"));        // dotless i is not ASCII 'i'
        Assert.Empty(Bind("\uFF29\uFF24: 1", "ID"));   // fullwidth letters are not ASCII letters
    }

    [Fact]
    public void Char_LongestLabelWins()
    {
        const string raw = "Full Name: x";

        var binding = Assert.Single(Bind(raw, "Name", "Full Name"));

        Assert.Equal("Full Name", binding.Label.Value);
    }

    [Theory]
    [InlineData("A:", FieldBindingTerminator.EndOfText)]
    [InlineData("A:;x", FieldBindingTerminator.Semicolon)]
    [InlineData("A:\nx", FieldBindingTerminator.Lf)]
    [InlineData("A:|x", FieldBindingTerminator.VerticalBar)]
    public void Char_EmptyValueIsRejectedWithItsOwnTerminator(string raw, FieldBindingTerminator terminator)
    {
        var binding = Assert.Single(Bind(raw, "A"));

        Assert.Equal(FieldBindingState.RejectedEmptyCandidate, binding.State);
        Assert.Equal(terminator, binding.Terminator);
    }

    [Fact]
    public void Char_LabelInsideAWordStillBinds_BecauseTheBinderIsGrammarFree() // UNSPECIFIED by D2.2: word-boundary rules are the caller's
    {
        const string raw = "Username: x";

        var binding = Assert.Single(Bind(raw, "Name"));

        Assert.Equal(new RawSpan(4, 4), binding.LabelSpan);
    }

    [Fact]
    public void Char_NoLabelsOrNoMatch_ReturnNothing_AndBadArgumentsThrow()
    {
        Assert.Empty(Bind("no labels here", "ZZTOP"));
        Assert.Empty(Bind(string.Empty, "A"));
        Assert.Empty(FieldBinder.Bind(NormalizedView.Build("A: x"), []));
        Assert.Throws<ArgumentNullException>(() => FieldBinder.Bind(null!, []));
        Assert.Throws<ArgumentNullException>(() => FieldBinder.Bind(NormalizedView.Build("x"), null!));
    }

    [Fact]
    public void Char_ResultIsDeterministicAndFreshPerCall()
    {
        const string raw = "A: one;B: two";

        var first = Bind(raw, "A", "B");
        var second = Bind(raw, "A", "B");

        Assert.Equal(first, second);
        Assert.NotSame(first, second);
    }
}
