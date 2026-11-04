using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.UsProductIntegration;
using static Privon.UsProductIntegration.Tests.UsProductHarness;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY. The defect: a valid labelled identifier was cut, and therefore missed, when its own letters spelled a
// recognised label ("HO-TEL-1234", "1234-SSN-5678" without the hyphens). The shared FieldBinder treated any label spelling
// inside the value as the START of the next field. D2.2 ends a field at the start of another recognised label; a spelling
// inside one alphanumeric token is not the start of a label.
//
// Each reproduction separates the five levels: (1) is the value valid for the grammar on its own, (2) what range did the REAL
// FieldBinder return, (3) did the detector produce a candidate, (4) what survives the real CandidateResolver, (5) what the
// real Level 3 policy does with it (the Protect render is in the App tests).
public class UsLabelBoundaryDefectTests
{
    private static IReadOnlyList<FieldBinding> BindWithCatalog(string raw) =>
        FieldBinder.Bind(
            NormalizedView.Build(raw),
            UsRecognizedLabels.All.Select(t => { FieldLabel.TryCreate(t, out var l); return l; }).ToList());

    private static IReadOnlyList<DetectionCandidate> Pipe(string raw) => UsOptInPipeline.Create().Detect(raw).Candidates;

    private static bool IsLegacyPassportToken(string v) =>
        v.Length is >= 6 and <= 12 && v.All(c => c <= 127 && char.IsAsciiLetterOrDigit(c)) && v.Any(char.IsAsciiDigit);

    private static bool IsI94Token(string v) => v.Length == 11 && v.All(c => c <= 127 && char.IsAsciiLetterOrDigit(c));

    // ===== the four reproductions, level by level ==========================================================================

    [Theory]
    [InlineData("Passport: HOTEL1234", "Passport", "HOTEL1234", "us.passport", "Tel")]
    [InlineData("Passport: XDLN1234", "Passport", "XDLN1234", "us.passport", "DLN")]
    [InlineData("Passport: 12MBI345", "Passport", "12MBI345", "us.passport", "MBI")]
    [InlineData("I-94: 1234SSN5678", "I-94", "1234SSN5678", "us.i94", "SSN")]
    public void TheDefect_IsFixed_AtEveryLevel(string raw, string label, string token, string detectorId, string insideLabel)
    {
        int start = raw.IndexOf(token, StringComparison.Ordinal);
        var span = new RawSpan(start, token.Length);

        // 1. the value is valid on its own, and it really does contain a recognised label spelling
        Assert.True(detectorId == "us.passport" ? IsLegacyPassportToken(token) : IsI94Token(token));
        Assert.Contains(insideLabel, token, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(UsRecognizedLabels.All, l => string.Equals(l, insideLabel, StringComparison.Ordinal));

        // 2. the real FieldBinder returns the whole token as the value, ended by the end of the text
        var binding = Assert.Single(BindWithCatalog(raw), b => b.Label.Value == label);
        Assert.Equal(span, binding.CandidateSpan);
        Assert.Equal(FieldBindingState.FieldAccepted, binding.State);
        Assert.Equal(FieldBindingTerminator.EndOfText, binding.Terminator);

        // 3. the detector produces the candidate with that exact span
        IDetector detector = detectorId == "us.passport" ? new UsPassportCandidateDetector() : new UsI94CandidateDetector();
        var detected = Assert.Single(Run(detector, raw));
        Assert.Equal(span, detected.Span);
        Assert.Equal(token.ToUpperInvariant(), detected.Canonical!.Value.Value);

        // 4. the same candidate survives the real opt-in pipeline (CandidateResolver included)
        var resolved = Assert.Single(Pipe(raw));
        Assert.Equal(detectorId, resolved.DetectorId.Value);
        Assert.Equal(span, resolved.Span);

        // 5. the real Level 3 policy: an interim decision, no alias yet
        Assert.Equal(RiskLevel.Level3, resolved.RiskLevel);
        var evaluated = new EvaluatedCandidate(resolved, TrustState.Unknown);
        Assert.Equal(CandidateDisposition.NeedsDecision, CandidatePolicyEvaluator.Decide(evaluated));
    }

    [Fact]
    public void TheOtherDetectorsDoNotInventCandidates_FromTheSpellingInsideTheToken()
    {
        // "SSN", "MBI", "Tel", "DLN" inside a token are neither fields nor candidates of their own type.
        foreach (string raw in new[] { "Passport: HOTEL1234", "Passport: XDLN1234", "Passport: 12MBI345", "I-94: 1234SSN5678" })
        {
            var types = Pipe(raw).Select(c => c.PiiType).ToList();

            Assert.DoesNotContain(PiiType.UsSsn, types);
            Assert.DoesNotContain(PiiType.UsMbi, types);
            Assert.DoesNotContain(PiiType.UsItin, types);
            Assert.DoesNotContain(PiiType.UsAtin, types);
            Assert.Single(types);
        }
    }

    // ===== GLUED labels: a value directly followed by the next approved label and its explicit separator ====================
    // The rule is a synthetic implementation assumption (not a D2.2 clause): a label that starts inside a token still ends the
    // field when ':' '#' '=' follows it immediately. The earlier value must still satisfy the detector's FULL grammar.

    [Theory]
    [InlineData("Passport: A12345678Tel: 555", "us.passport", "A12345678", "Tel")]
    [InlineData("A-Number: A1234567Phone: 5", "us.a_number", "A1234567", "Phone")]
    [InlineData("I-94: 123456789ABState: NY", "us.i94", "123456789AB", "State")]
    [InlineData("Passport: ZX81K2State: NY", "us.passport", "ZX81K2", "State")]
    public void Glued_TheEarlierValidIdentifier_IsDetected_AtLevelAfterLevel(string raw, string detectorId, string token, string nextLabel)
    {
        int start = raw.IndexOf(token, StringComparison.Ordinal);
        var span = new RawSpan(start, token.Length);
        int nextLabelStart = raw.IndexOf(nextLabel + ":", StringComparison.Ordinal);

        // 1. the earlier value is valid for its grammar on its own and the next approved label really follows it directly
        Assert.Equal(start + token.Length, nextLabelStart);
        Assert.Contains(UsRecognizedLabels.All, l => l == nextLabel);

        // 2. the real FieldBinder ends the first field exactly at the label start; the second field keeps its own value
        var bindings = BindWithCatalog(raw).OrderBy(b => b.LabelSpan.Start).ToList();
        Assert.Equal(span, bindings[0].CandidateSpan);
        Assert.Equal(FieldBindingTerminator.NextRecognizedLabel, bindings[0].Terminator);
        Assert.Equal(nextLabelStart, bindings[1].LabelSpan.Start);
        Assert.True(bindings[0].LogicalFieldSpan.End <= bindings[1].LabelSpan.Start);

        // 3. + 4. the detector and the real pipeline (CandidateResolver included) produce that exact span, nothing else
        var resolved = Assert.Single(Pipe(raw));
        Assert.Equal(detectorId, resolved.DetectorId.Value);
        Assert.Equal(span, resolved.Span);
        Assert.Equal(token.ToUpperInvariant(), resolved.Canonical!.Value.Value);

        // 5. Level 3 -> an interim decision
        Assert.Equal(RiskLevel.Level3, resolved.RiskLevel);
        Assert.Equal(CandidateDisposition.NeedsDecision,
            CandidatePolicyEvaluator.Decide(new EvaluatedCandidate(resolved, TrustState.Unknown)));
    }

    [Fact]
    public void Glued_AtinFollowedByAnSsn_KeepsBothExactSpans()
    {
        const string raw = "ATIN: 912-93-4567SSN: 321-54-9876";

        var found = Pipe(raw).OrderBy(c => c.Span.Start).ToList();

        Assert.Equal(
            [("us.atin", new RawSpan(6, 11)), ("us.ssn", new RawSpan(raw.IndexOf("321", StringComparison.Ordinal), 11))],
            found.Select(c => (c.DetectorId.Value, c.Span)));
        Assert.True(found[0].Span.End <= found[1].Span.Start);
        Assert.Equal("912-93-4567", Slice(raw, found[0]));
        Assert.Equal("321-54-9876", Slice(raw, found[1]));
    }

    [Fact]
    public void Glued_AnSsnFollowedByAName_KeepsTheSsn_EvenThoughTheNameValueIsNotAnIdentifier()
    {
        const string raw = "SSN: 321-54-9876Name: Jo";

        var ssn = Assert.Single(Pipe(raw));

        Assert.Equal(("us.ssn", new RawSpan(5, 11)), (ssn.DetectorId.Value, ssn.Span));
        Assert.Equal(CandidateDisposition.NeedsDecision, CandidatePolicyEvaluator.Decide(new EvaluatedCandidate(ssn, TrustState.Unknown)));
    }

    [Fact]
    public void Glued_TheFirstFourRepairedCases_StayRepaired()
    {
        foreach (var (raw, id) in new[] { ("Passport: HOTEL1234", "us.passport"), ("Passport: XDLN1234", "us.passport"),
                                          ("Passport: 12MBI345", "us.passport"), ("I-94: 1234SSN5678", "us.i94") })
        {
            Assert.Equal(id, Assert.Single(Pipe(raw)).DetectorId.Value);
        }
    }

    // ---- wrong candidates must not appear ----

    [Theory]
    [InlineData("Passport: Z123456789012Tel: 5")]          // LEGACY_TOKEN_LONGER_THAN_12 (13 characters) with a glued label
    [InlineData("Passport Number: Z1234Tel: 5")]            // LEGACY_TOKEN_SHORTER_THAN_6
    [InlineData("Passport Number: ABCDEFTel: 5")]           // no digit
    [InlineData("Passport Number: P1234567\uFF18Tel: 5")]   // fullwidth digit
    [InlineData("Passport: A12345678Tel 555")]              // glued label without a separator: one invalid token
    [InlineData("Passport: A12345678Tel555")]
    [InlineData("Passport: A12345678Tel : 555")]            // a space before the separator
    [InlineData("Passport: A12345678,Tel: 5")]              // punctuation the grammar does not allow
    [InlineData("Passport: A12345678!Tel: 5")]
    [InlineData("Passport: A12345678 and moreTel: 5")]      // a valid value followed by extra content
    [InlineData("Passport: A12345678\uFF34el: 5")]          // fullwidth T is not an ASCII label
    [InlineData("A-Number: B1234567Phone: 5")]              // wrong prefix
    [InlineData("A-Number: A123456Phone: 5")]               // six digits
    [InlineData("A-Number: A1234567890Phone: 5")]           // ten digits
    [InlineData("A-Number: A123456\uFF17Phone: 5")]         // fullwidth digit
    [InlineData("I-94: 123456789AState: NY")]               // ten characters
    [InlineData("I-94: 123456789ABCState: NY")]             // twelve characters
    [InlineData("I-94: X123456789ABYState: NY")]            // extra token content
    [InlineData("I-94: 123456789A\uFF22State: NY")]         // fullwidth letter
    public void Glued_AnInvalidOrAmbiguousEarlierValue_NeverBecomesACandidate(string raw)
    {
        Assert.DoesNotContain(Pipe(raw), c => c.PiiType is PiiType.UsPassport or PiiType.UsANumber or PiiType.UsI94);
    }

    [Fact]
    public void Glued_AnApprovedLabelSpellingAtTheEndOfAnotherWord_IsReadAsAGluedLabel_AmbiguityLeftUnresolved()
    {
        // CHARACTERIZATION, not a norm. "Hotel:" is not an approved label, but its tail "tel:" is the approved label Tel followed
        // by ':' and it starts inside the token -- exactly the glued form. The front part "A12345678Ho" is a complete valid legacy
        // value, so it is the candidate. D2.2 does not say whether such a word is a label or a word; this is the rule's literal
        // consequence and is reported as unresolved.
        const string raw = "Passport: A12345678Hotel: 5";

        var candidate = Assert.Single(Pipe(raw));

        Assert.Equal(new RawSpan(10, 11), candidate.Span);
    }

    [Fact]
    public void Glued_ThePartBeforeAnInWordLabelWithASeparator_MustItselfBeACompleteValue_NothingIsTruncatedIntoValidity()
    {
        // The field is cut at the label; the front part is judged by the full grammar. Six characters are a valid legacy value
        // (contract-literal); five are not and are never completed or promoted.
        Assert.Single(Pipe("Passport: ABC123Tel: 5"), c => c.PiiType == PiiType.UsPassport);
        Assert.DoesNotContain(Pipe("Passport: ABC12Tel: 5"), c => c.PiiType == PiiType.UsPassport);
    }

    [Fact]
    public void Glued_TheNinetySixCharacterLimitStillApplies_UpToTheLabelStart()
    {
        string at96 = "Passport: A12345678" + new string(' ', 96 - 19) + "Tel: 5";

        Assert.Single(Pipe(at96), c => c.PiiType == PiiType.UsPassport);
        Assert.DoesNotContain(Pipe("Passport: A12345678" + new string(' ', 96 - 18) + "Tel: 5"), c => c.PiiType == PiiType.UsPassport);
    }

    [Fact]
    public void Glued_EveryFrozenNegative_StaysNegative_WithAGluedLabelAppended()
    {
        foreach (string negative in new[]
                 { "Passport Number: Z1234", "Passport Number: Z123456789012", "Passport Number: ABCDEF", "Passport Number: P1234567\uFF18",
                   "A-Number: B1234567", "A-Number: A123456", "A-Number: A1234567890", "A-Number: A  1234567",
                   "I-94: 123456789A", "I-94: 123456789ABC", "I-94: X123456789ABY", "I-94: 123456789A\uFF22" })
        {
            Assert.DoesNotContain(Pipe(negative), c => c.PiiType is PiiType.UsPassport or PiiType.UsANumber or PiiType.UsI94);
            Assert.DoesNotContain(Pipe(negative + "Tel: 555"), c => c.PiiType is PiiType.UsPassport or PiiType.UsANumber or PiiType.UsI94);
            Assert.DoesNotContain(Pipe(negative + "State: NY"), c => c.PiiType is PiiType.UsPassport or PiiType.UsANumber or PiiType.UsI94);
        }
    }

    [Fact]
    public void Glued_ManyRepeatedGluedLabels_AndALongRun_AreBounded()
    {
        string many = string.Join("\n", Enumerable.Repeat("Passport: A12345678Tel: 555", 5_000));
        string run = "Passport: " + new string('x', 300_000) + "Tel: 5";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(5_000, Run(new UsPassportCandidateDetector(), many).Count);
        Assert.Empty(Run(new UsPassportCandidateDetector(), run));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Glued_TheSpansOfGluedFields_NeverOverlap_AcrossTheEightTypes()
    {
        const string raw =
            "Passport: A12345678Tel: 5\nA-Number: A1234567Phone: 5\nI-94: 123456789ABState: NY\n" +
            "ATIN: 912-93-4567SSN: 321-54-9876";

        var found = Pipe(raw).OrderBy(c => c.Span.Start).ToList();

        for (int i = 1; i < found.Count; i++)
        {
            Assert.True(found[i - 1].Span.End <= found[i].Span.Start);
        }

        Assert.Equal(
            ["us.passport", "us.a_number", "us.i94", "us.atin", "us.ssn"],
            found.Select(c => c.DetectorId.Value));
    }

    // ===== a real next field still ends the current one ====================================================================

    [Fact]
    public void TwoValidLabelledFields_OnOneLine_EachKeepTheirOwnExactSpan()
    {
        const string raw = "Passport: A12345678 SSN: 321-54-9876";

        var found = Pipe(raw).OrderBy(c => c.Span.Start).ToList();

        Assert.Equal(2, found.Count);
        Assert.Equal(("us.passport", new RawSpan(10, 9)), (found[0].DetectorId.Value, found[0].Span));
        Assert.Equal(("us.ssn", new RawSpan(raw.IndexOf("321", StringComparison.Ordinal), 11)), (found[1].DetectorId.Value, found[1].Span));
        Assert.Equal("A12345678", Slice(raw, found[0]));
        Assert.Equal("321-54-9876", Slice(raw, found[1]));
    }

    [Fact]
    public void TheFieldsInEitherOrder_AndWithTheOtherNewTypes_StayIndependent()
    {
        const string raw = "SSN: 321-54-9876 Passport: A12345678 A-Number: A1234567 I-94: 123456789AB Tel: 555";

        var found = Pipe(raw).OrderBy(c => c.Span.Start).Select(c => (c.DetectorId.Value, Slice(raw, c))).ToList();

        Assert.Equal(
            [("us.ssn", "321-54-9876"), ("us.passport", "A12345678"), ("us.a_number", "A1234567"), ("us.i94", "123456789AB")],
            found);
    }

    [Theory]
    [InlineData(";")]
    [InlineData("\t")]
    [InlineData("|")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData(" ")]
    public void EveryTerminator_StillEndsTheValue_BeforeAnInWordLabelSpellingToo(string terminator)
    {
        string raw = "Passport: HOTEL1234" + terminator + "I-94: 1234SSN5678";

        var found = Pipe(raw).OrderBy(c => c.Span.Start).ToList();

        Assert.Equal(["us.passport", "us.i94"], found.Select(c => c.DetectorId.Value));
        Assert.Equal(["HOTEL1234", "1234SSN5678"], found.Select(c => Slice(raw, c)));
    }

    [Fact]
    public void ALabelDirectlyFollowedByItsValue_StillEndsThePreviousField() // D2.2: zero spaces and no separator are legal
    {
        // The SSN detector itself does not accept "SSN321549876" (unchanged, independent of the binder), so the point checked
        // here is the passport side: its value ends before the label spelling and is not extended over the next field.
        const string raw = "Passport: A12345678 SSN321549876";

        var passport = Assert.Single(Pipe(raw));

        Assert.Equal(new RawSpan(10, 9), passport.Span);
        Assert.Equal("A12345678", Slice(raw, passport));
    }

    [Fact]
    public void AValueIsNeverAbsorbedWithTheNextField_EvenWhenTheNextFieldsLabelFollowsDirectlyAfterAnAsciiPunctuation()
    {
        const string raw = "Passport: HOTEL1234-SSN: 321-54-9876";

        var found = Pipe(raw).OrderBy(c => c.Span.Start).ToList();

        // 'HOTEL1234-' is not a token (the hyphen), so the passport value is not valid; the SSN keeps its own exact span.
        Assert.Equal(["us.ssn"], found.Select(c => c.DetectorId.Value));
        Assert.Equal("321-54-9876", Slice(raw, found[0]));
    }

    // ===== unapproved labels, token boundaries, invalid identifiers ========================================================

    [Theory]
    [InlineData("Passport ID: HOTEL1234")]               // label not in the closed list
    [InlineData("Passports: HOTEL1234")]                  // the label sits inside a longer word
    [InlineData("XPassport: HOTEL1234")]
    [InlineData("Hotel: HOTEL1234")]                      // an unapproved label string
    [InlineData("HOTEL1234")]                              // unlabelled
    [InlineData("Passport: ATEL1")]                        // too short (5)
    [InlineData("Passport: ABCDEFGHIJKTEL12")]             // too long (16)
    [InlineData("Passport: HOTELABC")]                     // no digit
    [InlineData("Passport: HOTEL123４")]               // fullwidth digit
    [InlineData("Passport: HOＴEL1234")]               // fullwidth T lookalike
    [InlineData("Passport: HOTEL1234 and more")]           // the whole bound value must be the token
    [InlineData("I-94: 1234SSN567")]                        // ten characters
    [InlineData("I-94: 1234SSN56789")]                      // twelve characters
    [InlineData("I-94: 1234SSN567８")]                  // fullwidth digit
    public void InvalidOrUnapprovedInputs_StayUndetected(string raw)
    {
        Assert.DoesNotContain(Pipe(raw), c => c.PiiType is PiiType.UsPassport or PiiType.UsI94);
    }

    [Theory]
    [InlineData("Passport:HOTEL1234")]
    [InlineData("Passport HOTEL1234")]
    [InlineData("Passport#HOTEL1234")]
    [InlineData("Passport=HOTEL1234")]
    [InlineData("Passport   :   HOTEL1234")]
    [InlineData("(Passport: HOTEL1234)")]
    [InlineData("\"Passport\": HOTEL1234")]
    public void TheLabelsOwnTokenBoundaries_AndTheSeparatorGrammar_AreUnchanged(string raw)
    {
        // "(Passport: HOTEL1234)" and quotes make the closing ')' / '"' part of the bound value (existing behaviour), so only
        // the forms whose value is exactly the token are detected.
        var found = Pipe(raw).Where(c => c.PiiType == PiiType.UsPassport).ToList();

        bool expected = !raw.Contains(')') && !raw.Contains("\": ");
        Assert.Equal(expected ? 1 : 0, found.Count);
    }

    [Fact]
    public void ASpanAfterEmojiAndSurrogatePairs_IsAnExactUtf16RawSpan_ForAnInWordLabelValue()
    {
        const string prefix = "\U0001F600\U0001F4A9 ";
        string raw = prefix + "Passport: HOTEL1234";

        var candidate = Assert.Single(Pipe(raw));

        Assert.Equal(new RawSpan(prefix.Length + 10, 9), candidate.Span);
        Assert.Equal("HOTEL1234", Slice(raw, candidate));
    }

    [Fact]
    public void TheNinetySixCharacterFieldLimit_StillApplies_ToAnInWordLabelValue()
    {
        string at96 = "Passport: HOTEL1234" + new string(' ', 96 - 19);

        Assert.Single(Pipe(at96));
        Assert.Empty(Pipe(at96 + " "));
    }

    [Fact]
    public void ARepeatedValue_IsOneCandidatePerOccurrence_WithOneCanonical_AndTheOrderIsStable()
    {
        const string raw = "Passport: HOTEL1234\nPassport: HOTEL1234\nI-94: 1234SSN5678";

        var forward = Pipe(raw).OrderBy(c => c.Span.Start).ToList();
        var reversed = UsOptInPipeline.Create(usDetectorFactory: () => UsIntegrationCandidates.CreateDetectors().Reverse().ToList())
            .Detect(raw).Candidates.OrderBy(c => c.Span.Start).ToList();

        Assert.Equal(3, forward.Count);
        Assert.Equal(forward[0].Canonical, forward[1].Canonical);
        Assert.Equal(forward.Select(c => (c.DetectorId.Value, c.Span)), reversed.Select(c => (c.DetectorId.Value, c.Span)));
        Assert.Equal(forward.Select(c => c.Span).Distinct().Count(), forward.Count);
    }

    [Fact]
    public void ManyRepeatedLabels_AndAVeryLongRun_AreBounded()
    {
        string many = string.Join("\n", Enumerable.Repeat("Passport: HOTEL1234", 5_000));
        string longRun = "Passport: " + new string('x', 300_000) + "TEL" + new string('y', 300_000);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var manyFound = Run(new UsPassportCandidateDetector(), many);
        var longFound = Run(new UsPassportCandidateDetector(), longRun);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "detection must stay near-linear");
        Assert.Equal(5_000, manyFound.Count);
        Assert.Empty(longFound); // an over-long field is rejected, not scanned for a "nearby" candidate
    }

    // ===== what must not change ============================================================================================

    [Fact]
    public void O1_TheCrossLabelTaxpayerCase_StillProtects_NextToTheNewFields()
    {
        const string raw = "ATIN: 912-88-4321 Passport: HOTEL1234";

        var found = Pipe(raw).OrderBy(c => c.Span.Start).ToList();

        Assert.Equal(["us.itin", "us.passport"], found.Select(c => c.DetectorId.Value));
        Assert.Equal(ResolutionEvidence.S, found[0].Evidence);
        Assert.Equal("HOTEL1234", Slice(raw, found[1]));
    }

    [Fact]
    public void TheFiveExistingTypes_AndTheEightWithRealLabels_KeepTheirFrozenRecords()
    {
        const string raw =
            "Passport Number: P12345678 State: NY\nA-Number: A1234567\nI-94 Number: 123456789AB\n" +
            "ATIN: 912-93-4567\nITIN: 912-70-1234\nSSN: 321-54-9876\nMBI: 1A23C45DE67\nSEVIS: N1234567890";

        Assert.Equal(8, Pipe(raw).Count);
        Assert.Equal(
            ["us.passport", "us.a_number", "us.i94", "us.atin", "us.itin", "us.ssn", "us.mbi", "us.sevis"],
            Pipe(raw).OrderBy(c => c.Span.Start).Select(c => c.DetectorId.Value));
    }

    [Fact]
    public void NoDiagnosticString_ShowsTheValue_ForTheNewlyDetectedTokens()
    {
        foreach (string secret in new[] { "HOTEL1234", "XDLN1234", "12MBI345", "1234SSN5678" })
        {
            string raw = secret.StartsWith("1234SSN", StringComparison.Ordinal) ? "I-94: " + secret : "Passport: " + secret;

            foreach (var c in Pipe(raw))
            {
                Assert.DoesNotContain(secret, c.ToString());
                Assert.DoesNotContain(secret, c.Canonical!.Value.ToString());
            }
        }
    }
}
