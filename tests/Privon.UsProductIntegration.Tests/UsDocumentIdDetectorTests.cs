using Privon.Core;
using Privon.Detection;
using Privon.UsProductIntegration;
using static Privon.UsProductIntegration.Tests.UsProductHarness;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY. us.passport (100), us.a_number (110) and us.i94 (120) through the REAL IDetector / DetectionCandidate
// contracts. Frozen D3A rows are reused with their expected values UNCHANGED (the row id is in the case name or a comment);
// cases the frozen oracle does not contain are synthetic and are marked "synthetic".
public class UsDocumentIdDetectorTests
{
    private const string Fullwidth7 = "７";
    private const string FullwidthB = "Ｂ";

    private static IReadOnlyList<DetectionCandidate> Passport(string raw) => Run(new UsPassportCandidateDetector(), raw);

    private static IReadOnlyList<DetectionCandidate> ANumber(string raw) => Run(new UsANumberCandidateDetector(), raw);

    private static IReadOnlyList<DetectionCandidate> I94(string raw) => Run(new UsI94CandidateDetector(), raw);

    // ===== identity and contract ============================================================================================

    [Fact]
    public void TheThreeDetectors_CarryTheFrozenIdentity_Rank_Type_AndReadiness()
    {
        (IDetector Detector, string Id, int Rank, PiiType Type)[] expected =
        [
            (new UsPassportCandidateDetector(), "us.passport", 100, PiiType.UsPassport),
            (new UsANumberCandidateDetector(), "us.a_number", 110, PiiType.UsANumber),
            (new UsI94CandidateDetector(), "us.i94", 120, PiiType.UsI94),
        ];

        foreach (var (detector, id, rank, type) in expected)
        {
            Assert.Equal(id, detector.Id.Value);
            Assert.Equal(rank, detector.ResolutionRank);
            Assert.Equal(type, detector.PiiType);
            Assert.Equal(DetectorReadinessState.Ready, detector.Readiness);
            // The real registry refuses any rank that differs from its frozen id -> rank table.
            Assert.Equal(rank, DetectorRegistry.Create([detector]).GetRank(detector.Id));
        }
    }

    [Fact]
    public void TheSupersededSpelling_UsAnumber_IsNotTheIdOfTheANumberDetector()
    {
        Assert.NotEqual("us.anumber", new UsANumberCandidateDetector().Id.Value);
        Assert.Equal("us.a_number", new UsANumberCandidateDetector().Id.Value);
    }

    [Fact]
    public void EveryCandidate_IsLevel3_HighConfidence_WithTheStampedIdentity_AndNoAliasYet()
    {
        var all = Passport("Passport Number: P12345678 State: NY")
            .Concat(ANumber("A-Number: A1234567"))
            .Concat(I94("I-94 Number: 123456789AB"))
            .ToList();

        Assert.Equal(3, all.Count);
        Assert.All(all, c =>
        {
            Assert.Equal(RiskLevel.Level3, c.RiskLevel);
            Assert.Equal(DetectionConfidence.High, c.Confidence);
            Assert.True(c.DetectorId.HasValue);
            Assert.Null(c.AliasIdentity); // assigned downstream, never by a detector
            Assert.NotNull(c.Canonical);
        });
    }

    [Fact]
    public void ADetector_RefusesANullContext_AndRunsOnEmptyText()
    {
        foreach (IDetector detector in new IDetector[] { new UsPassportCandidateDetector(), new UsANumberCandidateDetector(), new UsI94CandidateDetector() })
        {
            Assert.Throws<ArgumentNullException>(() => detector.Detect(null!));
            Assert.Empty(Run(detector, string.Empty));
            Assert.Empty(Run(detector, "오늘 날씨가 좋네요"));
        }
    }

    // ===== PASSPORT: frozen D3A rows =========================================================================================

    [Theory]
    // US-PAS-P01 / US-PAS-LBL02
    [InlineData("Passport Number: P12345678 State: NY", "us.passport|UsPassport|L3|S+C+J|17:9|P12345678|[여권번호1]|100|ownerClaim=true")]
    // US-PAS-P02: no J, conservative cross-jurisdiction protection only
    [InlineData("Passport Number: P12345678", "us.passport|UsPassport|L3|S+C|17:9|P12345678|[여권번호1]|100|ownerClaim=false")]
    // US-PAS-J01: route A, exact token plus valid J, no passport label
    [InlineData("State: NY P12345678", "us.passport|UsPassport|L3|S+J|10:9|P12345678|[여권번호1]|100|ownerClaim=true")]
    // US-PAS-P03: legacy labelled fallback
    [InlineData("Passport #: ZX81K2", "us.passport|UsPassport|L3|C|12:6|ZX81K2|[여권번호1]|100|ownerClaim=false")]
    // US-PAS-LBL01..05 (the closed label list)
    [InlineData("Passport: P12345678 State: NY", "us.passport|UsPassport|L3|S+C+J|10:9|P12345678|[여권번호1]|100|ownerClaim=true")]
    [InlineData("Passport No.: P12345678 State: NY", "us.passport|UsPassport|L3|S+C+J|14:9|P12345678|[여권번호1]|100|ownerClaim=true")]
    [InlineData("Passport #: P12345678 State: NY", "us.passport|UsPassport|L3|S+C+J|12:9|P12345678|[여권번호1]|100|ownerClaim=true")]
    [InlineData("U.S. Passport: P12345678", "us.passport|UsPassport|L3|S+C+J|15:9|P12345678|[여권번호1]|100|ownerClaim=true")]
    public void Passport_FrozenPositives_MatchTheOracleRecordExactly(string raw, string expected)
    {
        var candidate = Assert.Single(Passport(raw));

        Assert.Equal(expected, OracleWithOwner(candidate));
    }

    [Theory]
    [InlineData("Passport ID: P12345678")]                // US-PAS-LBL-N01: label not in the closed list, no J
    [InlineData("P12345678")]                              // US-PAS-FN01: unlabelled current format without J
    [InlineData("ZX81K2")]                                 // US-PAS-FN02: unlabelled legacy token
    [InlineData("Passport Number: Z1234")]                 // US-PAS-NEG-SHORT
    [InlineData("Passport Number: Z123456789012")]         // US-PAS-NEG-LONG
    [InlineData("Passport Number: ABCDEF")]                // US-PAS-NEG-ALLLETTER
    [InlineData("Passport Number: P1234567" + Fullwidth7)] // US-PAS-NEG-UNICODE
    [InlineData("Passport Number: Phone: ZX81K2")]         // US-PAS-NEG-NEXTLABEL
    public void Passport_FrozenNegatives_ProduceNoCandidate(string raw) => Assert.Empty(Passport(raw));

    // ===== PASSPORT: jurisdiction (J) ========================================================================================

    [Theory]
    [InlineData("United States P12345678")]
    [InlineData("USA P12345678")]
    [InlineData("U.S. P12345678")]
    [InlineData("New York P12345678")]
    [InlineData("district of columbia P12345678")]
    [InlineData("State: NY P12345678")]
    [InlineData("Issued by: CA P12345678")]
    [InlineData("Jurisdiction: TX P12345678")]
    [InlineData("State=PR P12345678")]
    [InlineData("P12345678 State: VI")]
    public void Passport_RouteA_AnExactTokenPlusJ_InTheSameLine_IsACandidate(string raw) // synthetic
    {
        var candidate = Assert.Single(Passport(raw));

        Assert.Equal(ResolutionEvidence.SJP, candidate.Evidence);
        Assert.Equal(OwnerClaim.True, candidate.OwnerClaim);
        Assert.Equal("P12345678", Slice(raw, candidate));
    }

    [Theory]
    [InlineData("NY P12345678")]                    // a bare abbreviation is not J
    [InlineData("State: ZZ P12345678")]             // not a USPS abbreviation
    [InlineData("State: NYC P12345678")]            // the abbreviation touches more letters
    [InlineData("State: ny P12345678")]             // [EA] the abbreviation must be upper-case
    [InlineData("State:    NY P12345678")]          // four spaces after the separator: the binding is rejected
    [InlineData("XState: NY P12345678")]            // the label sits inside a word
    [InlineData("Location: NY P12345678")]          // an equivalent label is not guessed
    [InlineData("State: NY\nP12345678")]            // the record is one physical line
    [InlineData("State: NY\rP12345678")]
    [InlineData("Sweet USAGE P12345678")]           // USA inside a word
    [InlineData("P12345678 and the Statesman")]     // no J
    public void Passport_RouteA_WithoutValidJ_IsNoCandidate(string raw) => Assert.Empty(Passport(raw)); // synthetic

    [Fact]
    public void Passport_ALongLine_DoesNotUndoAJurisdictionBinding_BecauseTheLimitEndsAtTheCandidate() // synthetic
    {
        string raw = "State: NY P12345678 " + new string('x', 200);

        var candidate = Assert.Single(Passport(raw));

        Assert.Equal(OwnerClaim.True, candidate.OwnerClaim);
    }

    [Fact]
    public void Passport_ALabelledToken_OnADifferentLineThanJ_KeepsOwnerClaimFalse() // synthetic
    {
        var candidate = Assert.Single(Passport("Passport: P12345678\nState: NY"));

        Assert.Equal(ResolutionEvidence.SC, candidate.Evidence);
        Assert.Equal(OwnerClaim.False, candidate.OwnerClaim);
    }

    [Fact]
    public void Passport_ALegacyToken_WithJ_IsCPlusJ_AndOwnerClaimTrue() // synthetic
    {
        var candidate = Assert.Single(Passport("Passport: ZX81K2 State: NY"));

        Assert.Equal("us.passport|UsPassport|L3|C+J|10:6|ZX81K2|[여권번호1]|100|ownerClaim=true", OracleWithOwner(candidate));
    }

    // ===== PASSPORT: grammar =================================================================================================

    [Theory]
    [InlineData("Passport: A123456", 10, 7)]            // legacy: 7 chars
    [InlineData("Passport: 123456", 10, 6)]             // legacy: all digits, minimum length
    [InlineData("Passport: 123456789012", 10, 12)]      // legacy: maximum length
    [InlineData("Passport: AB12CD34", 10, 8)]           // legacy: mixed
    [InlineData("Passport: P12345678", 10, 9)]          // current format
    [InlineData("PASSPORT NUMBER: P12345678", 17, 9)]   // ASCII-case-insensitive label
    [InlineData("passport = p12345678", 11, 9)]          // lower-case label and token
    public void Passport_LabelledTokens_AreAccepted_WithTheExactValueSpan(string raw, int start, int length) // synthetic
    {
        var candidate = Assert.Single(Passport(raw));

        Assert.Equal(new RawSpan(start, length), candidate.Span);
    }

    [Theory]
    [InlineData("Passport: P12345678X")]   // not the current format, but a 10-character alphanumeric token with digits: legacy
    [InlineData("Passport: XP12345678")]
    public void Passport_ALabelledTokenThatIsNotTheCurrentFormat_FallsBackToTheLegacyRule_WithCEvidenceOnly(string raw) // synthetic
    {
        var candidate = Assert.Single(Passport(raw));

        Assert.Equal(ResolutionEvidence.C, candidate.Evidence);
        Assert.Equal(OwnerClaim.False, candidate.OwnerClaim);
        Assert.Equal(10, candidate.Span.Length);
    }

    [Fact]
    public void Passport_ACanonicalValue_IsAsciiUpperCase() // synthetic
    {
        Assert.Equal("P12345678", Assert.Single(Passport("passport = p12345678")).Canonical!.Value.Value);
        Assert.Equal("ZX81K2", Assert.Single(Passport("Passport: zx81k2")).Canonical!.Value.Value);
    }

    [Theory]
    [InlineData("Passport: XP12345678XYZ")]             // 13 characters: neither current format nor a 6-12 legacy token
    [InlineData("Passport: P12345678 and more")]        // the whole bound value must be the token
    [InlineData("Passport: ABC-12345")]                  // not alphanumeric only
    [InlineData("Passport: ZX 81K2")]
    [InlineData("Passport:    P12345678")]              // four spaces after the separator
    [InlineData("Passport    : P12345678")]             // four spaces before it
    [InlineData("Passport - P12345678")]                // an arbitrary separator
    [InlineData("Passports: P12345678")]                // the label sits inside a longer word
    [InlineData("Mypassport: P12345678")]
    [InlineData("Passport: ")]                          // empty
    public void Passport_NearMisses_AreNoCandidate(string raw) => Assert.Empty(Passport(raw)); // synthetic

    [Theory]
    [InlineData("Passport: P12345678;memo")]
    [InlineData("Passport: P12345678\tmemo")]
    [InlineData("Passport: P12345678|memo")]
    [InlineData("Passport: P12345678\rmemo")]
    [InlineData("Passport: P12345678\nmemo")]
    [InlineData("Passport: P12345678\r\nmemo")]
    [InlineData("Passport: P12345678 Phone: 555")]       // next recognised label
    public void Passport_AFieldEndsAtItsTerminator(string raw) => Assert.Equal(new RawSpan(10, 9), Assert.Single(Passport(raw)).Span); // synthetic

    [Theory]
    [InlineData("Passport P12345678")]
    [InlineData("Passport:P12345678")]
    [InlineData("Passport#P12345678")]
    [InlineData("Passport=P12345678")]
    [InlineData("Passport   P12345678")]
    [InlineData("Passport   :   P12345678")]
    public void Passport_TheSeparatorGrammar_AcceptsNoneOrOneOfColonHashEquals_WithZeroToThreeSpaces(string raw) => Assert.Single(Passport(raw)); // synthetic

    [Fact]
    public void Passport_TheFieldLimitIs96RawCharacters_FromLabelStartThroughCandidateEnd() // synthetic, shared FieldBinder grammar
    {
        string at96 = "Passport: P12345678" + new string(' ', 96 - 19);
        string at97 = at96 + " ";

        Assert.Equal(96, at96.Length);
        Assert.Single(Passport(at96));
        Assert.Empty(Passport(at97));
    }

    [Fact]
    public void Passport_ASpanAfterEmojiAndSurrogatePairs_IsAnExactUtf16RawSpan() // synthetic
    {
        const string prefix = "\U0001F600\U0001F4A9 ";
        string raw = prefix + "Passport Number: P12345678 State: NY";

        var candidate = Assert.Single(Passport(raw));

        Assert.Equal(prefix.Length + 17, candidate.Span.Start);
        Assert.Equal("P12345678", Slice(raw, candidate));
    }

    [Fact]
    public void Passport_ARepeatedValue_IsOneCandidatePerOccurrence_WithTheSameCanonical() // synthetic
    {
        const string raw = "Passport: P12345678\nPassport: P12345678";

        var found = Passport(raw);

        Assert.Equal(2, found.Count);
        Assert.Equal(found[0].Canonical, found[1].Canonical);
        Assert.NotEqual(found[0].Span, found[1].Span);
    }

    [Fact]
    public void Passport_ATokenFoundByBothRoutes_IsOneCandidate_WithTheLabelledEvidence() // synthetic
    {
        var candidate = Assert.Single(Passport("U.S. Passport: P12345678 State: NY"));

        Assert.Equal(ResolutionEvidence.SCJP, candidate.Evidence);
    }

    // ===== A-NUMBER: frozen D3A rows =========================================================================================

    [Theory]
    [InlineData("A-Number: A1234567", "us.a_number|UsANumber|L3|S+C|10:8|A1234567|[A-NUMBER1]|110")]   // US-ANUM-P01
    [InlineData("A-Number: A 1234567", "us.a_number|UsANumber|L3|S+C|10:9|A1234567|[A-NUMBER1]|110")]  // US-ANUM-SPACE
    [InlineData("A-Number: A-1234567", "us.a_number|UsANumber|L3|S+C|10:9|A1234567|[A-NUMBER1]|110")]  // US-ANUM-HYPHEN
    public void ANumber_FrozenPositives_MatchTheOracleRecordExactly(string raw, string expected) =>
        Assert.Equal(expected, Oracle(Assert.Single(ANumber(raw))));

    [Theory]
    [InlineData("A-Number: A123456")]                  // US-ANUM-FN01: six digits are an intended miss
    [InlineData("A1234567")]                           // US-ANUM-FN02: unlabelled
    [InlineData("A-Number: B1234567")]                 // US-ANUM-NEG-PREFIX
    [InlineData("A-Number: A123456" + Fullwidth7)]     // US-ANUM-NEG-UNICODE
    [InlineData("A-Number: XA1234567Y")]               // US-ANUM-NEG-EMBED
    [InlineData("A-Number: A  1234567")]               // US-ANUM-NEG-DOUBLESP
    [InlineData("A-Number: A1234567890")]              // US-ANUM-NEG-LONG
    [InlineData("A-Number: Phone: A1234567")]          // US-ANUM-NEG-NEXTLABEL
    public void ANumber_FrozenNegatives_ProduceNoCandidate(string raw) => Assert.Empty(ANumber(raw));

    // ===== A-NUMBER: synthetic ===============================================================================================

    [Theory]
    [InlineData("A-Number: A1234567", 10, 8)]
    [InlineData("A Number: A1234567", 10, 8)]
    [InlineData("Alien Registration Number: A1234567", 27, 8)]
    [InlineData("USCIS Number: A1234567", 14, 8)]
    [InlineData("uscis number = a1234567", 15, 8)]
    [InlineData("A-Number: A12345678", 10, 9)]         // eight digits
    [InlineData("A-Number: A123456789", 10, 10)]       // nine digits
    [InlineData("A-Number: 1234567", 10, 7)]           // the 'A' is optional
    [InlineData("A-Number: 123456789", 10, 9)]
    public void ANumber_EveryApprovedLabelAndLength_IsAccepted(string raw, int start, int length)
    {
        var candidate = Assert.Single(ANumber(raw));

        Assert.Equal(new RawSpan(start, length), candidate.Span);
    }

    [Fact]
    public void ANumber_TheCanonicalValue_AlwaysCarriesTheAPrefix_SoEveryFormSharesOneIdentity() // [EA] documented
    {
        string[] raws = ["A-Number: A1234567", "A-Number: 1234567", "A-Number: a-1234567", "USCIS Number: A 1234567"];

        var canon = raws.Select(r => Assert.Single(ANumber(r)).Canonical!.Value.Value).Distinct().ToList();

        Assert.Equal(["A1234567"], canon);
    }

    [Theory]
    [InlineData("Alien Number: A1234567")]             // not an approved label
    [InlineData("A#: A1234567")]
    [InlineData("Registration Number: A1234567")]
    [InlineData("File Number: A1234567")]
    [InlineData("Number: A1234567")]
    [InlineData("Data Number: A1234567")]              // the label would sit inside a word
    [InlineData("A-Numbers: A1234567")]
    [InlineData("A-Number: Ａ1234567")]            // fullwidth A
    [InlineData("A-Number: A 1234567")]           // no-break space separator
    [InlineData("A-Number: A–1234567")]           // en dash separator
    [InlineData("A-Number: A_1234567")]
    [InlineData("A-Number: A- 1234567")]
    [InlineData("A-Number: A1234567 and more")]
    [InlineData("A-Number: ١٢٣٤٥٦٧")] // Arabic-Indic digits
    [InlineData("A-Number:    A1234567")]
    [InlineData("A-Number: ")]
    public void ANumber_NearMissesAndUnapprovedForms_AreNoCandidate(string raw) => Assert.Empty(ANumber(raw)); // synthetic

    [Fact]
    public void ANumber_ASpanAfterEmoji_IsAnExactUtf16RawSpan() // synthetic
    {
        const string prefix = "\U0001F600 ";
        string raw = prefix + "A-Number: A 1234567";

        var candidate = Assert.Single(ANumber(raw));

        Assert.Equal(new RawSpan(prefix.Length + 10, 9), candidate.Span);
        Assert.Equal("A 1234567", Slice(raw, candidate));
    }

    [Fact]
    public void ANumber_TheSameValueTwice_IsTwoCandidates_WithOneCanonical() // synthetic
    {
        var found = ANumber("A-Number: A1234567\nUSCIS Number: A-1234567");

        Assert.Equal(2, found.Count);
        Assert.Equal(found[0].Canonical, found[1].Canonical);
    }

    [Fact]
    public void ANumber_TheFieldLimitIs96RawCharacters() // synthetic
    {
        string at96 = "A-Number: A1234567" + new string(' ', 96 - 18);

        Assert.Single(ANumber(at96));
        Assert.Empty(ANumber(at96 + " "));
    }

    [Theory]
    [InlineData("A-Number: A1234567;memo")]
    [InlineData("A-Number: A1234567\tmemo")]
    [InlineData("A-Number: A1234567|memo")]
    [InlineData("A-Number: A1234567\nmemo")]
    [InlineData("A-Number: A1234567 Phone: 555")]
    public void ANumber_AFieldEndsAtItsTerminator(string raw) => Assert.Equal(new RawSpan(10, 8), Assert.Single(ANumber(raw)).Span); // synthetic

    // ===== I-94: frozen D3A rows =============================================================================================

    [Theory]
    [InlineData("I-94 Number: 123456789AB", "us.i94|UsI94|L3|S+C|13:11|123456789AB|[I-941]|120")]            // US-I94-P01
    [InlineData("I-94: 123456789AB", "us.i94|UsI94|L3|S+C|6:11|123456789AB|[I-941]|120")]                    // US-I94-LBL01
    [InlineData("I94: 123456789AB", "us.i94|UsI94|L3|S+C|5:11|123456789AB|[I-941]|120")]                     // US-I94-LBL02
    [InlineData("I94 Number: 123456789AB", "us.i94|UsI94|L3|S+C|12:11|123456789AB|[I-941]|120")]             // US-I94-LBL03
    [InlineData("I-94 Admission Number: 123456789AB", "us.i94|UsI94|L3|S+C|23:11|123456789AB|[I-941]|120")]  // US-I94-LBL04
    [InlineData("Form I-94 Number: 123456789AB", "us.i94|UsI94|L3|S+C|18:11|123456789AB|[I-941]|120")]       // US-I94-LBL05
    public void I94_FrozenPositives_MatchTheOracleRecordExactly(string raw, string expected) =>
        Assert.Equal(expected, Oracle(Assert.Single(I94(raw))));

    [Theory]
    [InlineData("Admission Number: 123456789AB")]      // US-I94-N01: the generic label is out
    [InlineData("I-94: 123456789A")]                   // US-I94-LEN10
    [InlineData("I-94: 123456789ABC")]                 // US-I94-LEN12
    [InlineData("I-94: X123456789ABY")]                // US-I94-BOUND
    [InlineData("I-94: 123456789A" + FullwidthB)]      // US-I94-UNICODE
    [InlineData("I-94 Number: Phone: 123456789AB")]    // US-I94-NEG-NEXTLABEL
    public void I94_FrozenNegatives_ProduceNoCandidate(string raw) => Assert.Empty(I94(raw));

    // ===== I-94: synthetic ===================================================================================================

    [Theory]
    [InlineData("I-94: 12345678901")]                  // eleven digits
    [InlineData("I-94: ABCDEFGHIJK")]                  // eleven letters
    [InlineData("i-94 number = 123456789ab")]
    public void I94_AnyElevenAsciiAlphanumerics_AreAccepted(string raw) => Assert.Equal(11, Assert.Single(I94(raw)).Span.Length);

    [Fact]
    public void I94_TheCanonicalValue_IsAsciiUpperCase() =>
        Assert.Equal("123456789AB", Assert.Single(I94("i94: 123456789ab")).Canonical!.Value.Value);

    [Theory]
    [InlineData("I-94 No.: 123456789AB")]               // not an approved label
    [InlineData("I 94: 123456789AB")]
    [InlineData("I-94 Record: 123456789AB")]
    [InlineData("Arrival Record: 123456789AB")]
    [InlineData("Admission: 123456789AB")]
    [InlineData("123456789AB")]                          // unlabelled
    [InlineData("I-94: 123456789 AB")]                   // a space inside the token
    [InlineData("I-94: 123456-89AB")]
    [InlineData("I-94: 123456789AB and more")]
    [InlineData("I-94 arrival yesterday, number 123456789AB")] // free text with travel words only
    [InlineData("I entered the U.S. last week, Admission Number 123456789AB")]
    [InlineData("I-94:    123456789AB")]
    [InlineData("XI-94: 123456789AB")]                   // the label sits inside a word
    [InlineData("I-94: ")]
    public void I94_NearMissesAndUnapprovedForms_AreNoCandidate(string raw) => Assert.Empty(I94(raw)); // synthetic

    [Fact]
    public void I94_ASpanAfterEmoji_IsAnExactUtf16RawSpan() // synthetic
    {
        const string prefix = "\U0001F9F3\U0001F600 ";
        string raw = prefix + "Form I-94 Number: 123456789AB";

        var candidate = Assert.Single(I94(raw));

        Assert.Equal(new RawSpan(prefix.Length + 18, 11), candidate.Span);
        Assert.Equal("123456789AB", Slice(raw, candidate));
    }

    [Fact]
    public void I94_TheFieldLimitIs96RawCharacters() // synthetic
    {
        string at96 = "I-94: 123456789AB" + new string(' ', 96 - 17);

        Assert.Single(I94(at96));
        Assert.Empty(I94(at96 + " "));
    }

    [Theory]
    [InlineData("I-94: 123456789AB;memo")]
    [InlineData("I-94: 123456789AB\tmemo")]
    [InlineData("I-94: 123456789AB|memo")]
    [InlineData("I-94: 123456789AB\nmemo")]
    [InlineData("I-94: 123456789AB Phone: 555")]
    public void I94_AFieldEndsAtItsTerminator(string raw) => Assert.Equal(new RawSpan(6, 11), Assert.Single(I94(raw)).Span); // synthetic

    // ===== independence between the three ====================================================================================

    [Fact]
    public void EachDetector_ReportsOnlyItsOwnIdentifier_OnAMixedText() // synthetic
    {
        const string raw = "Passport: P12345678; A-Number: A1234567; I-94: 123456789AB";

        Assert.Equal(["us.passport"], Passport(raw).Select(c => c.DetectorId.Value));
        Assert.Equal(["us.a_number"], ANumber(raw).Select(c => c.DetectorId.Value));
        Assert.Equal(["us.i94"], I94(raw).Select(c => c.DetectorId.Value));
    }

    [Fact]
    public void TheEightDetectors_OnTheFiveExistingFixtures_ReportExactlyTheFiveFrozenRecords() // the five stay unchanged
    {
        const string raw = "ATIN: 912-93-4567\nITIN: 912-70-1234\nSSN: 321-54-9876\nMBI: 1A23C45DE67\nSEVIS: N1234567890";

        var viaFive = AllFive().SelectMany(d => Run(d, raw)).Select(Oracle).Order(StringComparer.Ordinal).ToList();
        var viaEight = AllEight().SelectMany(d => Run(d, raw)).Select(Oracle).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(viaFive, viaEight);
        Assert.Equal(5, viaEight.Count);
    }

    // ===== no raw value in any diagnostic string =============================================================================

    [Theory]
    [InlineData("Passport Number: P12345678 State: NY", "P12345678")]
    [InlineData("Passport: ZX81K2", "ZX81K2")]
    [InlineData("A-Number: A 1234567", "1234567")]
    [InlineData("I-94 Number: 123456789AB", "123456789AB")]
    public void NoDiagnosticString_ContainsTheRawValue(string raw, string secret)
    {
        var candidates = Passport(raw).Concat(ANumber(raw)).Concat(I94(raw)).ToList();
        Assert.NotEmpty(candidates);

        foreach (var c in candidates)
        {
            Assert.DoesNotContain(secret, c.ToString());
            Assert.DoesNotContain(secret, c.Canonical!.Value.ToString());
            Assert.DoesNotContain(secret, c.DetectorName);
            Assert.DoesNotContain(secret, c.DetectorId.ToString());
        }

        foreach (IDetector d in new IDetector[] { new UsPassportCandidateDetector(), new UsANumberCandidateDetector(), new UsI94CandidateDetector() })
        {
            Assert.DoesNotContain(secret, d.Name);
            Assert.DoesNotContain(secret, d.ToString());
        }
    }

    [Fact]
    public void TheInternalMatchRecord_NeverPrintsItsCanonicalValue()
    {
        // The record is internal and no public surface prints it, so its own ToString is checked directly.
        var assembly = typeof(UsPassportCandidateDetector).Assembly;
        var matchType = assembly.GetType("Privon.UsProductIntegration.UsDocumentMatch", throwOnError: true)!;
        var evidenceType = assembly.GetType("Privon.UsProductIntegration.UsDocumentEvidence", throwOnError: true)!;
        var match = Activator.CreateInstance(
            matchType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            binder: null, args: [new RawSpan(0, 9), Enum.ToObject(evidenceType, 3), "P12345678", OwnerClaim.True, RiskLevel.Level3, DetectionConfidence.High], culture: null)!;

        string text = match.ToString()!;

        Assert.DoesNotContain("P12345678", text);
        Assert.DoesNotContain("12345678", text);
    }

    [Fact]
    public void ADetectorFailure_IsAnExceptionThatNeverCarriesTheText()
    {
        // The only input the detectors reject is a null context; the message must not contain any scanned text.
        var ex = Assert.Throws<ArgumentNullException>(() => new UsPassportCandidateDetector().Detect(null!));

        Assert.DoesNotContain("P12345678", ex.Message);
    }
}
