using Privon.Detection;
using Privon.UsSsn;

namespace Privon.UsSsn.Tests;

// SYNTHETIC DATA ONLY. EXPERIMENT ONLY. Contract basis (Notion "040-US-D2.2", read-only):
//   * a logical field is one recognized label plus its single bound value; binding ends at the earliest of
//     CR, LF, ';', HT, '|' or the start of another recognized field label; at most 96 raw characters;
//   * D3-NEG negatives assert ZERO candidates from the TARGET detector;
//   * family resolution is ATIN > ITIN > SSN with mutual exclusion;
//   * strong ATIN context = "ATIN" / "Adoption Taxpayer Identification Number".
// The local frozen D3A corpus agrees with all of the above for the cases it contains.
public class UsTaxpayerContractAlignmentTests
{
    private static readonly string[] TaxLabels = ["ITIN", "ATIN", "SSN"];

    private static UsTaxpayerKind KindOf(string label) => label switch
    {
        "ITIN" => UsTaxpayerKind.Itin,
        "ATIN" => UsTaxpayerKind.Atin,
        _ => UsTaxpayerKind.Ssn,
    };

    private static string Valid(UsTaxpayerKind kind) => kind switch
    {
        UsTaxpayerKind.Itin => "912-70-1234",
        UsTaxpayerKind.Atin => "912-93-4567",
        _ => "321-54-9876",
    };

    // ---- ATIN / ITIN / SSN strong labels ----

    [Fact]
    public void DefaultLabelSet_ContainsExactlyTheContractStrongLabels()
    {
        var set = UsTaxpayerLabelSet.Default;

        Assert.Equal(["ATIN", "Adoption Taxpayer Identification Number"], set.Atin.OrderBy(s => s.Length).ToArray());
        Assert.Equal(["ITIN"], set.Itin.ToArray());
        Assert.Equal(["SSN", "Social Security Number"], set.Ssn.OrderBy(s => s.Length).ToArray());
    }

    [Theory]
    [InlineData("Adoption Taxpayer Identification Number: 912-93-4567", 41, 11, UsSsnForm.Dashed)]
    [InlineData("Adoption Taxpayer Identification Number: 912934567", 41, 9, UsSsnForm.Compact)]
    [InlineData("Adoption Taxpayer Identification Number: 912 93 4567", 41, 11, UsSsnForm.Spaced)]
    [InlineData("adoption taxpayer identification number=912934567", 40, 9, UsSsnForm.Compact)]
    [InlineData("ADOPTION TAXPAYER IDENTIFICATION NUMBER #912-93-4567", 41, 11, UsSsnForm.Dashed)]
    public void AtinLongLabel_BindsLikeTheShortLabel(string raw, int start, int length, UsSsnForm form)
    {
        var candidate = Assert.Single(UsTaxpayerFamily.Evaluate(raw).Candidates);

        Assert.Equal(UsTaxpayerKind.Atin, candidate.Kind);
        Assert.Equal(new RawSpan(start, length), candidate.Span);
        Assert.Equal(UsSsnEvidence.Structure | UsSsnEvidence.Context, candidate.Evidence);
        Assert.Equal(form, candidate.Form);
    }

    [Theory]
    [InlineData("Adoption Taxpayer: 912934567")]
    [InlineData("Taxpayer Identification Number: 912934567")]
    [InlineData("Adoption Taxpayer Identification: 912934567")]
    [InlineData("XAdoption Taxpayer Identification Number: 912934567")]
    [InlineData("Adoption Taxpayer Identification Numbers: 912934567")]
    [InlineData("Adoption Taxpayer Identification Number 9 912934567")]
    public void PartialOrEmbeddedAtinLabels_AreNotStrongContext(string raw)
    {
        Assert.Empty(UsTaxpayerFamily.Evaluate(raw).Candidates);
    }

    [Fact]
    public void AtinLongLabel_IsTargetedByTheAtinDetectorOnly()
    {
        const string raw = "Adoption Taxpayer Identification Number: 912934567";

        Assert.Single(UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, UsTaxpayerKind.Atin).Candidates);
        Assert.Empty(UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, UsTaxpayerKind.Itin).Candidates);
        Assert.Empty(UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, UsTaxpayerKind.Ssn).Candidates);
    }

    // ---- generic field ownership (no hard-coded case): a value bound to another recognized field is that field's ----

    public static IEnumerable<object[]> TaxLabelWithOtherLabel()
    {
        foreach (var tax in TaxLabels)
        foreach (var other in new[] { "Phone", "Name", "Address", "I-94", "ZZTOP" })
        {
            yield return [tax, other];
        }
    }

    [Theory]
    [MemberData(nameof(TaxLabelWithOtherLabel))]
    public void FieldWhoseFirstTokenIsAnotherRecognizedLabel_YieldsNothing_ForEveryLabelPair(string taxLabel, string otherLabel)
    {
        var kind = KindOf(taxLabel);
        var labels = UsTaxpayerLabelSet.Default.WithOthers("Phone", "ZZTOP");
        string raw = $"{taxLabel}: {otherLabel}: {Valid(kind)}";

        var family = UsTaxpayerFamily.EvaluateTaxFamilyResolver(raw, labels);
        var target = UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, kind, labels);

        Assert.Empty(family.Candidates);
        Assert.Empty(target.Candidates);
        Assert.Contains(target.Attempts, a => a.Kind == kind && a.Reason == UsTaxpayerRejectionReason.NextRecognizedFieldTerminatesBinding);
    }

    [Theory]
    [InlineData("Phone: 912-70-1234")]
    [InlineData("Name: 912-70-1234")]
    [InlineData("Address: 912-93-4567")]
    [InlineData("I-94: 321-54-9876")]
    [InlineData("ZZTOP: 912-70-1234")]
    [InlineData("Phone: call 912-70-1234 today")]
    public void NumberInsideANonTaxRecognizedField_IsNotReinterpretedAsAnUnlabeledTaxpayerNumber(string raw)
    {
        var labels = UsTaxpayerLabelSet.Default.WithOthers("Phone", "ZZTOP");

        Assert.Empty(UsTaxpayerFamily.EvaluateTaxFamilyResolver(raw, labels).Candidates);
        foreach (var kind in Enum.GetValues<UsTaxpayerKind>())
        {
            Assert.Empty(UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, kind, labels).Candidates);
        }
    }

    [Theory] // independent fields / positions keep their candidates
    [InlineData("Phone: 2125551212; ITIN: 912-70-1234", UsTaxpayerKind.Itin, 25, UsSsnEvidence.Structure | UsSsnEvidence.Context)]
    [InlineData("Phone: 2125551212; 912-70-1234", UsTaxpayerKind.Itin, 19, UsSsnEvidence.Structure)]
    [InlineData("Phone: 2125551212\n912-93-4567", UsTaxpayerKind.Atin, 18, UsSsnEvidence.Structure)]
    [InlineData("Phone: 2125551212\t321-54-9876", UsTaxpayerKind.Ssn, 18, UsSsnEvidence.Structure)]
    [InlineData("Phone: 2125551212|321-54-9876", UsTaxpayerKind.Ssn, 18, UsSsnEvidence.Structure)]
    [InlineData("912-70-1234 Phone: 2125551212", UsTaxpayerKind.Itin, 0, UsSsnEvidence.Structure)]
    [InlineData("Name: Bob; SSN: 321-54-9876", UsTaxpayerKind.Ssn, 16, UsSsnEvidence.Structure | UsSsnEvidence.Context)]
    [InlineData("Phone: ITIN: 912-70-1234", UsTaxpayerKind.Itin, 13, UsSsnEvidence.Structure | UsSsnEvidence.Context)]
    [InlineData("Username: 912-70-1234", UsTaxpayerKind.Itin, 10, UsSsnEvidence.Structure)] // "name" inside a word is not a label
    public void IndependentFieldsAndPositions_KeepTheirCandidates(string raw, UsTaxpayerKind kind, int start, UsSsnEvidence evidence)
    {
        var labels = UsTaxpayerLabelSet.Default.WithOthers("Phone");

        var family = Assert.Single(UsTaxpayerFamily.EvaluateTaxFamilyResolver(raw, labels).Candidates);
        var target = Assert.Single(UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, kind, labels).Candidates);

        Assert.Equal(kind, family.Kind);
        Assert.Equal(start, family.Span.Start);
        Assert.Equal(evidence, family.Evidence);
        Assert.Equal(family.Span, target.Span);
        Assert.Equal(family.Evidence, target.Evidence);
    }

    [Fact]
    public void ConfirmedD22NonTaxLabels_AreRecognizedByDefault_AndTerminateTaxFields()
    {
        // I-94, address and name labels are enumerated in D2.2; a tax field ends where they begin.
        var result = UsTaxpayerFamily.Evaluate("SSN: 321549876 Full Name: Pat ITIN: 912701234");

        Assert.Equal([UsTaxpayerKind.Ssn, UsTaxpayerKind.Itin], result.Candidates.Select(c => c.Kind).ToArray());
    }

    [Fact]
    public void LabelSetsThatCannotBeFullyEnumerated_AreAnExplicitInputNotAHiddenAssumption()
    {
        // Labels of other detectors (phone, bank, A-Number, ...) are not enumerable from this API; without them the
        // field is not recognized as owned (documented limitation), with them the contract behavior applies.
        const string raw = "Phone: 912-70-1234";

        Assert.Single(UsTaxpayerFamily.EvaluateTaxFamilyResolver(raw).Candidates);
        Assert.Empty(UsTaxpayerFamily.EvaluateTaxFamilyResolver(raw, UsTaxpayerLabelSet.Default.WithOthers("Phone")).Candidates);
    }

    // ---- explicit modes ----

    [Fact]
    public void Mode_TargetVersusFamily_ForTheSameText_ATIN_912_92_1234()
    {
        const string raw = "ATIN: 912-92-1234";

        var atin = UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, UsTaxpayerKind.Atin);
        var itin = UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, UsTaxpayerKind.Itin);
        var family = UsTaxpayerFamily.EvaluateTaxFamilyResolver(raw);

        Assert.Empty(atin.Candidates); // D3-NEG: zero candidates from the target detector
        Assert.Contains(atin.Attempts, a => a.Reason == UsTaxpayerRejectionReason.AtinRequires93);
        Assert.Empty(itin.Candidates); // the value belongs to the ATIN field, so it is not reinterpreted
        Assert.Equal(UsTaxpayerKind.Itin, Assert.Single(family.Candidates).Kind); // family resolution may legitimately emit ITIN
    }

    [Fact]
    public void Mode_ITIN_912_93_1234_TargetsEmitNothing_FamilyEmitsAtin()
    {
        const string raw = "ITIN: 912-93-1234";

        Assert.Empty(UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, UsTaxpayerKind.Itin).Candidates);
        Assert.Empty(UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, UsTaxpayerKind.Atin).Candidates);
        Assert.Equal(UsTaxpayerKind.Atin, Assert.Single(UsTaxpayerFamily.EvaluateTaxFamilyResolver(raw).Candidates).Kind);
    }

    [Fact]
    public void Mode_UnlabeledDashedNumber_EachTargetJudgesItOnItsOwn()
    {
        const string raw = "912-93-4567";

        Assert.Single(UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, UsTaxpayerKind.Atin).Candidates);
        var itin = UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, UsTaxpayerKind.Itin);
        var ssn = UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, UsTaxpayerKind.Ssn);
        Assert.Empty(itin.Candidates);
        Assert.Empty(ssn.Candidates);
        Assert.Equal(UsTaxpayerRejectionReason.ItinRange93Excluded, Assert.Single(itin.Attempts).Reason);
        Assert.Equal(UsTaxpayerRejectionReason.Area900To999, Assert.Single(ssn.Attempts).Reason);
        Assert.All(ssn.Attempts.Concat(itin.Attempts), a => Assert.Equal(UsTaxpayerAttemptOutcome.Rejected, a.Outcome));
    }

    [Fact]
    public void Mode_TargetEvaluation_ReportsOnlyTheTargetType()
    {
        foreach (var raw in new[] { "ATIN: 912-93-4567", "912-70-1234", "SSN: 321-54-9876; ITIN: 912701234", "X912-70-1234Y" })
        foreach (var kind in Enum.GetValues<UsTaxpayerKind>())
        {
            var result = UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, kind);

            Assert.All(result.Candidates, c => Assert.Equal(kind, c.Kind));
            Assert.All(result.Attempts, a => Assert.Equal(kind, a.Kind));
        }
    }

    [Fact]
    public void Mode_TargetEvaluation_ForAForeignLabelledValue_HasNoAttemptsAtAll()
    {
        var result = UsTaxpayerFamily.EvaluateTargetDetectorOnly("ATIN: 912-93-4567", UsTaxpayerKind.Itin);

        Assert.Empty(result.Candidates);
        Assert.Empty(result.Attempts);
    }

    [Fact]
    public void Mode_IndependentFields_ArePreservedPerTargetAndInTheFamily()
    {
        const string raw = "ITIN: 912701234; ATIN: 912934567; SSN: 321549876";

        foreach (var kind in Enum.GetValues<UsTaxpayerKind>())
        {
            Assert.Equal(kind, Assert.Single(UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, kind).Candidates).Kind);
        }

        Assert.Equal(3, UsTaxpayerFamily.EvaluateTaxFamilyResolver(raw).Candidates.Count);
    }

    [Fact]
    public void Mode_TargetEvaluation_IsolatesFieldsEvenWithoutTerminators()
    {
        const string raw = "ITIN: 912701234 ATIN: 912934567";

        var itin = Assert.Single(UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, UsTaxpayerKind.Itin).Candidates);
        var atin = Assert.Single(UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, UsTaxpayerKind.Atin).Candidates);

        Assert.Equal("912701234", raw.Substring(itin.Span.Start, itin.Span.Length));
        Assert.Equal("912934567", raw.Substring(atin.Span.Start, atin.Span.Length));
    }

    [Theory]
    [InlineData("ITIN: 912-70-1234", "ITIN: 912-70-1234")]
    [InlineData("912-93-4567 and ATIN: 912934567;", "x")]
    [InlineData("SSN: 321549876; ITIN: 912-65-1234; 321-54-9876", "x")]
    [InlineData("X912-70-1234Y 000-12-3456", "x")]
    [InlineData("ATIN: 912-92-1234", "x")]
    public void Evaluate_KeepsTheEstablishedFamilyBehavior(string raw, string _)
    {
        var legacy = UsTaxpayerFamily.Evaluate(raw);
        var family = UsTaxpayerFamily.EvaluateTaxFamilyResolver(raw);

        Assert.Equal(legacy.Candidates, family.Candidates);
        Assert.Equal(legacy.Attempts, family.Attempts);
    }

    [Fact]
    public void Mode_Arguments_AreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UsTaxpayerFamily.EvaluateTargetDetectorOnly("ITIN: 912701234", (UsTaxpayerKind)999));
        Assert.Throws<ArgumentNullException>(() => UsTaxpayerFamily.EvaluateTargetDetectorOnly("x", UsTaxpayerKind.Ssn, null!));
        Assert.Throws<ArgumentNullException>(() => UsTaxpayerFamily.EvaluateTaxFamilyResolver("x", null!));
        Assert.Empty(UsTaxpayerFamily.EvaluateTargetDetectorOnly(null, UsTaxpayerKind.Atin).Candidates);
        Assert.Empty(UsTaxpayerFamily.EvaluateTaxFamilyResolver("").Candidates);
    }

    // ---- the frozen D3A cases under their CONTRACT mode (TargetDetectorOnly = zero target candidates, strictly) ----

    [Theory]
    [InlineData("US-ITIN-RN01", "ITIN: 912-49-1234", UsTaxpayerKind.Itin)]
    [InlineData("US-ITIN-RN02", "ITIN: 912-66-1234", UsTaxpayerKind.Itin)]
    [InlineData("US-ITIN-RN03", "ITIN: 912-69-1234", UsTaxpayerKind.Itin)]
    [InlineData("US-ITIN-RN04", "ITIN: 912-89-1234", UsTaxpayerKind.Itin)]
    [InlineData("US-ITIN-RN05", "ITIN: 912-93-1234", UsTaxpayerKind.Itin)]
    [InlineData("US-ATIN-N92", "ATIN: 912-92-1234", UsTaxpayerKind.Atin)]
    [InlineData("US-ATIN-N94", "ATIN: 912-94-1234", UsTaxpayerKind.Atin)]
    [InlineData("US-ITIN-NEG-DOT", "ITIN: 912.70.1234", UsTaxpayerKind.Itin)]
    [InlineData("US-ATIN-NEG-DOT", "ATIN: 912.93.1234", UsTaxpayerKind.Atin)]
    [InlineData("US-ITIN-NEG-EMBED", "X912-70-1234Y", UsTaxpayerKind.Itin)]
    [InlineData("US-ATIN-NEG-EMBED", "X912-93-1234Y", UsTaxpayerKind.Atin)]
    [InlineData("US-ITIN-NEG-UNICODE", "ITIN: 912-70-123４", UsTaxpayerKind.Itin)]
    [InlineData("US-ATIN-NEG-UNICODE", "ATIN: 912-93-123４", UsTaxpayerKind.Atin)]
    [InlineData("US-ATIN-NEG-LEN", "ATIN: 912-93-123", UsTaxpayerKind.Atin)]
    [InlineData("US-ITIN-FN01", "912701234", UsTaxpayerKind.Itin)]
    [InlineData("US-ATIN-FN01", "912934567", UsTaxpayerKind.Atin)]
    [InlineData("US-SSN-FN01", "321549876", UsTaxpayerKind.Ssn)]
    [InlineData("US-SSN-N01", "SSN: 000-54-9876", UsTaxpayerKind.Ssn)]
    [InlineData("US-SSN-XN01", "666-54-9876", UsTaxpayerKind.Ssn)]
    [InlineData("US-SSN-XN02", "900-54-9876", UsTaxpayerKind.Ssn)]
    [InlineData("US-SSN-XN03", "321-00-9876", UsTaxpayerKind.Ssn)]
    [InlineData("US-SSN-XN04", "321-54-0000", UsTaxpayerKind.Ssn)]
    [InlineData("US-SSN-XN05", "SSN: 321.54.9876", UsTaxpayerKind.Ssn)]
    [InlineData("US-SSN-XN06", "SSN: 321-54-987６", UsTaxpayerKind.Ssn)]
    [InlineData("US-SSN-XN07", "X321-54-9876Y", UsTaxpayerKind.Ssn)]
    public void FrozenNegatives_ZeroCandidatesFromTheTargetDetector_InTargetMode(string caseId, string raw, UsTaxpayerKind target)
    {
        var result = UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, target);

        Assert.True(result.Candidates.Count == 0, caseId);
    }

    [Fact]
    public void FrozenNegative_ITIN_NEG_NEXTLABEL_ZeroTargetCandidates_InTargetMode()
    {
        var labels = UsTaxpayerLabelSet.Default.WithOthers("Phone");

        var result = UsTaxpayerFamily.EvaluateTargetDetectorOnly("ITIN: Phone: 912-70-1234", UsTaxpayerKind.Itin, labels);

        Assert.Empty(result.Candidates);
    }

    [Theory]
    [InlineData("ATIN: 912934567", UsTaxpayerKind.Atin, "us.atin|UsAtin|L3|S+C|6:9|912934567")]
    [InlineData("ATIN: 912 93 4567", UsTaxpayerKind.Atin, "us.atin|UsAtin|L3|S+C|6:11|912934567")]
    [InlineData("ITIN: 912701234", UsTaxpayerKind.Itin, "us.itin|UsItin|L3|S+C|6:9|912701234")]
    [InlineData("ITIN: 912-65-1234", UsTaxpayerKind.Itin, "us.itin|UsItin|L3|S+C|6:11|912651234")]
    [InlineData("SSN: 321-54-9876", UsTaxpayerKind.Ssn, "us.ssn|UsSsn|L3|S+C|5:11|321549876")]
    [InlineData("912-93-4567", UsTaxpayerKind.Atin, "us.atin|UsAtin|L3|S|0:11|912934567")]
    [InlineData("Social Security Number: 321 54 9876", UsTaxpayerKind.Ssn, "us.ssn|UsSsn|L3|S+C|24:11|321549876")]
    public void FrozenPositives_AreIdenticalInTargetMode(string raw, UsTaxpayerKind target, string expected)
    {
        var c = Assert.Single(UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, target).Candidates);
        string kind = c.Kind switch { UsTaxpayerKind.Atin => "atin|UsAtin", UsTaxpayerKind.Itin => "itin|UsItin", _ => "ssn|UsSsn" };
        string ev = c.Evidence == (UsSsnEvidence.Structure | UsSsnEvidence.Context) ? "S+C" : "S";

        Assert.Equal(expected, $"us.{kind}|L3|{ev}|{c.Span.Start}:{c.Span.Length}|{c.Canonical.GetDigitsForTestingOnly()}");
    }

    [Theory]
    [InlineData("SSN: 321-54-9876")]
    [InlineData("321-54-9876")]
    [InlineData("SSN: 321549876;memo")]
    [InlineData("SSN: 321549876 thanks")]
    [InlineData("SSN: 321-54-9876 thanks")]
    [InlineData("CLASSN: 321549876")]
    [InlineData("X321-54-9876Y")]
    [InlineData("SSN: 321-54 9876")]
    public void TargetSsn_MatchesTheStandaloneSsnModule(string raw)
    {
        var standalone = UsSsnScanner.Detect(raw).Candidates
            .Select(c => (c.Span, c.Evidence, c.Form, c.Canonical.GetDigitsForTestingOnly())).ToArray();
        var target = UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, UsTaxpayerKind.Ssn).Candidates
            .Select(c => (c.Span, c.Evidence, c.Form, c.Canonical.GetDigitsForTestingOnly())).ToArray();

        Assert.Equal(standalone, target);
    }

    // ---- common properties hold in both modes ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothModes_AreDeterministic_ReadOnly_AndRedacted(bool targetMode)
    {
        const string raw = "ITIN: 912-70-1234; ATIN: 912.93.1234; SSN: 321-54-9876; 987-93-6543";
        UsTaxpayerFamilyResult Run() => targetMode
            ? UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, UsTaxpayerKind.Ssn)
            : UsTaxpayerFamily.EvaluateTaxFamilyResolver(raw);

        var first = Run();
        var second = Run();

        Assert.Equal(first.Candidates, second.Candidates);
        Assert.Equal(first.Attempts, second.Attempts);
        Assert.Throws<NotSupportedException>(() => ((IList<UsTaxpayerCandidate>)first.Candidates).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<UsTaxpayerAttempt>)first.Attempts).Clear());
        foreach (var text in first.Candidates.Select(c => c.ToString()).Concat(first.Attempts.Select(a => a.ToString())).Append(first.ToString()))
        {
            foreach (var secret in new[] { "912", "987", "321", "1234", "6543", "9876" })
            {
                Assert.DoesNotContain(secret, text);
            }
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)] // beyond the former 64-restart limit
    [InlineData(5_000)]
    [InlineData(50_000)]
    public void AdjacentLabelFlood_NeverHidesALaterField_AndStaysNearLinear(int count)
    {
        // A flood of back-to-back labels must neither be slow nor silently end label collection: a legitimate field after
        // the flood is still found, in both modes, and "nothing found" is never the way an unfinished scan is reported.
        string text = string.Concat(Enumerable.Repeat("Phone: ", count)) + "ITIN: 912-70-1234";
        var labels = UsTaxpayerLabelSet.Default.WithOthers("Phone");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var family = UsTaxpayerFamily.EvaluateTaxFamilyResolver(text, labels);
        var target = UsTaxpayerFamily.EvaluateTargetDetectorOnly(text, UsTaxpayerKind.Itin, labels);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "evaluation must stay near-linear");
        foreach (var candidate in new[] { Assert.Single(family.Candidates), Assert.Single(target.Candidates) })
        {
            Assert.Equal(UsTaxpayerKind.Itin, candidate.Kind);
            Assert.Equal(UsSsnEvidence.Structure | UsSsnEvidence.Context, candidate.Evidence);
            Assert.Equal("912-70-1234", text.Substring(candidate.Span.Start, candidate.Span.Length));
        }
    }

    [Fact]
    public void NoSilentIncompleteState_ThereIsNoRestartLimitToExhaust()
    {
        // The earlier implementation stopped label collection after a fixed number of re-binds and reported what it had
        // found so far as if it were complete. The shared binder now ends a field at a label that starts the value, so
        // no re-binding (and therefore no limit) exists.
        Assert.Null(typeof(UsTaxpayerFamily).GetField("MaxFieldRestarts"));
    }

    [Fact]
    public void BothModes_ReportExactRawSpans()
    {
        const string raw = "ref  ITIN:  912-70-1234 ;ATIN=912 93 4567|SSN 321549876";
        var family = UsTaxpayerFamily.EvaluateTaxFamilyResolver(raw);

        Assert.Equal(
            ["912-70-1234", "912 93 4567", "321549876"],
            family.Candidates.Select(c => raw.Substring(c.Span.Start, c.Span.Length)).ToArray());
        foreach (var kind in Enum.GetValues<UsTaxpayerKind>())
        {
            var single = Assert.Single(UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, kind).Candidates);
            Assert.Equal(family.Candidates.Single(c => c.Kind == kind).Span, single.Span);
        }
    }

    [Fact]
    public void BothModes_HandleHostileInputWithoutFailure()
    {
        foreach (var raw in new[] { "\0", "\uD800", "ITIN:", "ITIN: ITIN: ITIN:", "ATIN:\r\n:", new string(':', 5000), "Phone: Phone: Phone: 912-70-1234" })
        {
            UsTaxpayerFamily.EvaluateTaxFamilyResolver(raw, UsTaxpayerLabelSet.Default.WithOthers("Phone"));
            foreach (var kind in Enum.GetValues<UsTaxpayerKind>())
            {
                UsTaxpayerFamily.EvaluateTargetDetectorOnly(raw, kind, UsTaxpayerLabelSet.Default.WithOthers("Phone"));
            }
        }
    }
}
