using Privon.Core;
using Privon.Detection;
using Privon.Detection.Scope;
using Privon.UsProductIntegration;
using static Privon.UsProductIntegration.Tests.UsProductHarness;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY. us.driver_state_id (130) and us.bank_account (140) through the REAL IDetector / DetectionCandidate
// contracts, the opt-in DetectionPipeline, the per-country scope, and the real Level 3 policy and alias stages. Frozen D3A rows
// are reused with their expected values UNCHANGED (the row id is in a comment); everything else is marked "synthetic".
// "120000003" is the frozen algorithmic synthetic routing token (valid prefix and 3-7-1 checksum, no real-bank claim).
public class UsLicenseAndBankTests
{
    private const string Fullwidth7 = "７";
    private const string Fullwidth3 = "３";
    private const string Routing = "120000003";

    private static IReadOnlyList<DetectionCandidate> Dl(string raw) => Run(new UsDriverStateIdCandidateDetector(), raw);

    private static IReadOnlyList<DetectionCandidate> Bank(string raw) => Run(new UsBankAccountCandidateDetector(), raw);

    private static IReadOnlyList<DetectionCandidate> Pipe(string raw) => UsOptInPipeline.Create().Detect(raw).Candidates;

    /// <summary>A routing number with a valid 3-7-1 check digit for the given 8 leading digits.</summary>
    private static string Aba(string first8)
    {
        int[] d = first8.Select(c => c - '0').ToArray();
        int sum = 3 * (d[0] + d[3] + d[6]) + 7 * (d[1] + d[4] + d[7]) + (d[2] + d[5]);
        return first8 + ((10 - sum % 10) % 10);
    }

    // ===== identity ========================================================================================================

    [Fact]
    public void TheTwoDetectors_CarryTheFrozenIdentity_Rank_Type_AndReadiness()
    {
        (IDetector Detector, string Id, int Rank, PiiType Type)[] expected =
        [
            (new UsDriverStateIdCandidateDetector(), "us.driver_state_id", 130, PiiType.UsDriverStateId),
            (new UsBankAccountCandidateDetector(), "us.bank_account", 140, PiiType.BankAccount),
        ];

        foreach (var (detector, id, rank, type) in expected)
        {
            Assert.Equal(id, detector.Id.Value);
            Assert.Equal(rank, detector.ResolutionRank);
            Assert.Equal(type, detector.PiiType);
            Assert.Equal(DetectorReadinessState.Ready, detector.Readiness);
            Assert.Equal(rank, DetectorRegistry.Create([detector]).GetRank(detector.Id)); // the real registry's frozen table
        }
    }

    [Fact]
    public void TheBankAccountType_IsTheFrozenBankAccount_NotTheKoreanBankAccountNumber()
    {
        Assert.Equal(PiiType.BankAccount, new UsBankAccountCandidateDetector().PiiType);
        Assert.NotEqual(PiiType.BankAccountNumber, new UsBankAccountCandidateDetector().PiiType);
        Assert.Equal("[계좌1]", new AliasIdentity(AliasFamilyTable.FamilyOf(PiiType.BankAccount), 1).CanonicalContractToken);
        Assert.Equal("[운전면허번호1]", new AliasIdentity(AliasFamilyTable.FamilyOf(PiiType.UsDriverStateId), 1).CanonicalContractToken);
    }

    [Fact]
    public void ADetector_RefusesANullContext_AndRunsOnEmptyText()
    {
        foreach (IDetector d in new IDetector[] { new UsDriverStateIdCandidateDetector(), new UsBankAccountCandidateDetector() })
        {
            Assert.Throws<ArgumentNullException>(() => d.Detect(null!));
            Assert.Empty(Run(d, string.Empty));
            Assert.Empty(Run(d, "오늘 날씨가 좋네요"));
        }
    }

    // ===== DRIVER LICENSE / STATE ID: frozen D3A rows =======================================================================

    [Theory]
    [InlineData("Driver License Number: D123-4567 State: CA", "us.driver_state_id|UsDriverStateId|L3|S+C+J|23:9|D1234567|[운전면허번호1]|130")] // US-DL-P01
    [InlineData("Driver License: D1234567 State: CA", "us.driver_state_id|UsDriverStateId|L3|S+C+J|16:8|D1234567|[운전면허번호1]|130")]         // LBL01
    [InlineData("Driver's License: D1234567 State: CA", "us.driver_state_id|UsDriverStateId|L3|S+C+J|18:8|D1234567|[운전면허번호1]|130")]       // LBL02
    [InlineData("Driver License Number: D1234567 State: CA", "us.driver_state_id|UsDriverStateId|L3|S+C+J|23:8|D1234567|[운전면허번호1]|130")]  // LBL03
    [InlineData("Driver's License Number: D1234567 State: CA", "us.driver_state_id|UsDriverStateId|L3|S+C+J|25:8|D1234567|[운전면허번호1]|130")] // LBL04
    [InlineData("DL Number: D1234567 State: CA", "us.driver_state_id|UsDriverStateId|L3|S+C+J|11:8|D1234567|[운전면허번호1]|130")]               // LBL05
    [InlineData("DLN: D1234567 State: CA", "us.driver_state_id|UsDriverStateId|L3|S+C+J|5:8|D1234567|[운전면허번호1]|130")]                     // LBL06
    [InlineData("State ID: D1234567 State: CA", "us.driver_state_id|UsDriverStateId|L3|S+C+J|10:8|D1234567|[운전면허번호1]|130")]               // LBL07
    [InlineData("State ID Number: D1234567 State: CA", "us.driver_state_id|UsDriverStateId|L3|S+C+J|17:8|D1234567|[운전면허번호1]|130")]       // LBL08
    [InlineData("Document Type: Driver License; Customer ID Number: D1234567; State: CA", "us.driver_state_id|UsDriverStateId|L3|S+C+J|51:8|D1234567|[운전면허번호1]|130")] // LBL09
    public void DriverStateId_FrozenPositives_MatchTheOracleRecordExactly(string raw, string expected) =>
        Assert.Equal(expected, Oracle(Assert.Single(Dl(raw))));

    [Theory]
    [InlineData("Driver License Number: D123-4567")]                      // US-DL-FN01: no J
    [InlineData("License Number: D1234567 State: CA")]                    // US-DL-LBL-N01
    [InlineData("Customer ID Number: D1234567 State: CA")]                // US-DL-LBL-N02
    [InlineData("Driver License: D123  4567 State: CA")]                  // NEG-DOUBLESP
    [InlineData("Driver License: D123/4567 State: CA")]                   // NEG-PUNCT
    [InlineData("Driver License: D1234567890123456789012345 State: CA")]  // NEG-LONG (26)
    [InlineData("Driver License: ABCDEFGH State: CA")]                    // NEG-ALLLETTER
    [InlineData("Driver License: D1234567 CA")]                           // NEG-BAREJ
    [InlineData("Driver License: D123456" + Fullwidth7 + " State: CA")]   // NEG-UNICODE
    public void DriverStateId_FrozenNegatives_ProduceNoCandidate(string raw) => Assert.Empty(Dl(raw));

    // ===== DRIVER LICENSE: jurisdiction J ===================================================================================

    [Theory]
    [InlineData("Driver License: D1234567; United States")]
    [InlineData("Driver License: D1234567 | USA")]
    [InlineData("Driver License: D1234567;U.S.")]
    [InlineData("Driver License: D1234567\tNew York")]
    [InlineData("Driver License: D1234567; Puerto Rico")]
    [InlineData("California Driver License: D1234567")]
    [InlineData("Driver License: D1234567 Issued by: NY")]
    [InlineData("Driver License: D1234567 Jurisdiction: TX")]
    [InlineData("State: NY Driver License: D1234567")]
    public void DriverStateId_AnyAcceptedFormOfJ_InTheSameLine_AllowsTheCandidate(string raw) // synthetic
    {
        var candidate = Assert.Single(Dl(raw));

        Assert.Equal(ResolutionEvidence.SCJP, candidate.Evidence);
        Assert.Equal("D1234567", Slice(raw, candidate));
    }

    [Fact]
    public void DriverStateId_AValueWithInternalSpaces_CannotBeSplitFromTrailingWords_UnresolvedAmbiguityPreserved()
    {
        // CHARACTERIZATION, not a norm. A value may hold single internal spaces, so "D1234567 in the United States" is itself a
        // valid 25-character value: the field cannot be split from the words after it without a terminator, and the J words then
        // belong to the value. D2.2 does not decide this; the candidate covers the whole remainder. (The shared binder also ends a
        // field at the label spelling "State" inside "United States", which is why "USA" is used here.)
        const string raw = "Driver License: D1234567 in the USA";

        var candidate = Assert.Single(Dl(raw));

        Assert.Equal(new RawSpan(16, raw.Length - 16), candidate.Span);
    }

    [Theory]
    [InlineData("Driver License: D1234567 NY")]               // a bare abbreviation
    [InlineData("Driver License: D1234567 State: ZZ")]        // not a USPS abbreviation
    [InlineData("Driver License: D1234567 State: NYC")]
    [InlineData("Driver License: D1234567\nState: CA")]       // J on another line: the record is one physical line
    [InlineData("State: CA\nDriver License: D1234567")]
    [InlineData("Driver License: D1234567 State:    CA")]     // four spaces: the J binding is rejected
    [InlineData("Driver License: D1234567 Location: CA")]     // an equivalent label is not guessed
    public void DriverStateId_WithoutValidJ_IsNoCandidate(string raw) => Assert.Empty(Dl(raw)); // synthetic

    // ===== DRIVER LICENSE: grammar ==========================================================================================

    [Theory]
    [InlineData("Driver License: D123 State: CA", 16, 4)]                              // 4 characters: the minimum
    [InlineData("Driver License: 1234 State: CA", 16, 4)]
    [InlineData("Driver License: D1234567890123456789012 State: CA", 16, 23)]          // 23
    [InlineData("Driver License: ABCDEFGHIJKLMNOPQRSTUVWX1 State: CA", 16, 25)]        // 25: the maximum
    [InlineData("Driver License: D123 4567 State: CA", 16, 9)]                         // one internal space
    [InlineData("Driver License: D123-45-67 State: CA", 16, 10)]                       // hyphens
    [InlineData("Driver License: D12 345-678 State: CA", 16, 11)]                      // a space and a hyphen
    [InlineData("driver license = d1234567 State: CA", 17, 8)]                         // [EA] lower-case label and value
    public void DriverStateId_Grammar_Accepts(string raw, int start, int length) // synthetic
    {
        var candidate = Assert.Single(Dl(raw));

        Assert.Equal(new RawSpan(start, length), candidate.Span);
    }

    [Theory]
    [InlineData("Driver License: D12 State: CA")]               // 3
    [InlineData("Driver License: ABCDEFGHIJKLMNOPQRSTUVWXY1Z State: CA")] // 27
    [InlineData("Driver License: ABCDEFGHIJKLMNOPQRSTUVWXYZ State: CA")]  // no digit
    [InlineData("Driver License: -D1234567 State: CA")]         // a leading separator
    [InlineData("Driver License: D1234567- State: CA")]         // a trailing hyphen
    [InlineData("Driver License: D123 -4567 State: CA")]        // two separators in a row
    [InlineData("Driver License: D123--4567 State: CA")]
    [InlineData("Driver License: D123_4567 State: CA")]
    [InlineData("Driver License: D123.4567 State: CA")]
    [InlineData("Driver License: D123 4567 State: CA")]    // no-break space
    [InlineData("Driver License: D123–4567 State: CA")]    // en dash
    [InlineData("Driver License: Ｄ" + "1234567 State: CA")] // fullwidth D
    [InlineData("Driver Licence: D1234567 State: CA")]          // not an approved label
    [InlineData("ID Number: D1234567 State: CA")]
    [InlineData("Driver License Numbers: D1234567 State: CA")]
    [InlineData("Mydriver license: D1234567 State: CA")]
    [InlineData("D1234567 State: CA")]                          // unlabelled
    [InlineData("Driver License: State: CA")]                   // empty
    public void DriverStateId_NearMisses_AreNoCandidate(string raw) => Assert.Empty(Dl(raw)); // synthetic

    [Fact]
    public void DriverStateId_TheCanonicalValue_RemovesSeparators_AndIsAsciiUpperCase() // [EA] upper-case folding, as for the others
    {
        Assert.Equal("D1234567", Assert.Single(Dl("DL Number: d123-45 67 State: CA")).Canonical!.Value.Value);
    }

    [Theory]
    [InlineData("Driver License: D1234567;memo State: CA")]
    [InlineData("Driver License: D1234567\tState: CA")]
    [InlineData("Driver License: D1234567|memo State: CA")]
    public void DriverStateId_AFieldEndsAtItsTerminator_AndJStillComesFromTheSameLine(string raw)
    {
        Assert.Equal(new RawSpan(16, 8), Assert.Single(Dl(raw)).Span);
    }

    [Fact]
    public void DriverStateId_TheFieldLimitIs96RawCharacters_FromLabelStartThroughTheEndOfTheBoundField() // shared FieldBinder reading
    {
        string at96 = "Driver License: D1234567" + new string(' ', 96 - 24) + "State: CA";

        Assert.Equal(96, at96.IndexOf("State:", StringComparison.Ordinal));
        Assert.Single(Dl(at96));
        Assert.Empty(Dl(at96.Replace("State:", " State:", StringComparison.Ordinal)));
    }

    [Fact]
    public void DriverStateId_SpansAfterEmojiAreExactUtf16RawSpans_AndRepeatsAreSeparateCandidates()
    {
        const string prefix = "\U0001F600\U0001F4A9 ";
        string raw = prefix + "DLN: D123-4567 State: CA\nDLN: D1234567 State: CA";

        var found = Dl(raw);

        Assert.Equal(2, found.Count);
        Assert.Equal(new RawSpan(prefix.Length + 5, 9), found[0].Span);
        Assert.Equal("D123-4567", Slice(raw, found[0]));
        Assert.Equal(found[0].Canonical, found[1].Canonical);
    }

    // ===== DRIVER LICENSE: Customer ID Number needs a companion AND J ======================================================

    [Theory]
    [InlineData("Document Type: Driver License; Customer ID Number: D1234567; State: CA")]
    [InlineData("Document Type: State ID; Customer ID Number: D1234567; State: CA")]
    [InlineData("Customer ID Number: D1234567; Document Type: State ID; State: CA")]
    [InlineData("Document Type: driver license | Customer ID Number: D1234567 | State: CA")]
    public void CustomerId_WithAnExplicitCompanion_AndJ_IsACandidate(string raw) // synthetic
    {
        var candidate = Assert.Single(Dl(raw));

        Assert.Equal("D1234567", Slice(raw, candidate));
    }

    [Theory]
    [InlineData("Customer ID Number: D1234567; State: CA")]                                   // no companion
    [InlineData("Document Type: Driver License; Customer ID Number: D1234567")]               // no J
    [InlineData("Document Type: Passport; Customer ID Number: D1234567; State: CA")]          // another document type
    [InlineData("Document Type: ID Card; Customer ID Number: D1234567; State: CA")]           // no synonym is inferred
    [InlineData("Document Type: Driver License\nCustomer ID Number: D1234567; State: CA")]    // companion on another line
    [InlineData("Document Type: Driver License; Customer ID Number: D1234567\nState: CA")]    // J on another line
    [InlineData("Document Type: Driver License Plus; Customer ID Number: D1234567; State: CA")]
    public void CustomerId_WithoutBothCompanionAndJ_IsNoCandidate(string raw) => Assert.Empty(Dl(raw)); // synthetic

    [Fact]
    public void CustomerId_TwoCompanionFields_AreNotExactlyOne_AmbiguityLeftUnresolved() // [EA_UNRESOLVED] characterization
    {
        const string raw = "Document Type: Driver License; Document Type: State ID; Customer ID Number: D1234567; State: CA";

        Assert.Empty(Dl(raw));
    }

    // ===== BANK: frozen D3A rows ============================================================================================

    [Theory]
    [InlineData("Bank Account Number: AB12CD34", "us.bank_account|BankAccount|L3|C|21:8|AB12CD34|[계좌1]|140")]                  // US-BANK-P01
    [InlineData("Bank Account: AB12CD34", "us.bank_account|BankAccount|L3|C|14:8|AB12CD34|[계좌1]|140")]                         // LBL-BANK
    [InlineData("Checking Account: AB12CD34", "us.bank_account|BankAccount|L3|C|18:8|AB12CD34|[계좌1]|140")]                     // LBL-CHECK
    [InlineData("Checking Account Number: AB12CD34", "us.bank_account|BankAccount|L3|C|25:8|AB12CD34|[계좌1]|140")]              // LBL-CHECKNO
    [InlineData("Savings Account: AB12CD34", "us.bank_account|BankAccount|L3|C|17:8|AB12CD34|[계좌1]|140")]                      // LBL-SAVE
    [InlineData("Savings Account Number: AB12CD34", "us.bank_account|BankAccount|L3|C|24:8|AB12CD34|[계좌1]|140")]               // LBL-SAVENO
    [InlineData("Bank Account Number: AB12CD34EF56GH789", "us.bank_account|BankAccount|L3|C|21:17|AB12CD34EF56GH789|[계좌1]|140")] // MAX17
    [InlineData("Routing Number: 120000003 | Account Number: AB12CD34", "us.bank_account|BankAccount|L3|S+C+P|44:8|AB12CD34|[계좌1]|140")]            // P02 / RT-ROUTING
    [InlineData("ABA Routing Number: 120000003 | Account Number: AB12CD34", "us.bank_account|BankAccount|L3|S+C+P|48:8|AB12CD34|[계좌1]|140")]        // RT-ABA
    [InlineData("ABA Number: 120000003 | Account Number: AB12CD34", "us.bank_account|BankAccount|L3|S+C+P|40:8|AB12CD34|[계좌1]|140")]              // RT-ABANO
    [InlineData("Routing Transit Number: 120000003 | Account Number: AB12CD34", "us.bank_account|BankAccount|L3|S+C+P|52:8|AB12CD34|[계좌1]|140")]    // RT-TRANSIT
    [InlineData("Routing Number: 120000003\nAccount Number: AB12CD34", "us.bank_account|BankAccount|L3|S+C+P|42:8|AB12CD34|[계좌1]|140")]            // 2LINE
    [InlineData("Account Number: AB12CD34\nRouting Number: 120000003", "us.bank_account|BankAccount|L3|S+C+P|16:8|AB12CD34|[계좌1]|140")]            // 2LINE-REV
    [InlineData("Account Number: AB12CD34 | Routing Number: 120000003", "us.bank_account|BankAccount|L3|S+C+P|16:8|AB12CD34|[계좌1]|140")]          // 1LINE-REV
    public void Bank_FrozenPositives_MatchTheOracleRecordExactly(string raw, string expected) =>
        Assert.Equal(expected, Oracle(Assert.Single(Bank(raw))));

    [Fact]
    public void Bank_TheDistanceBoundary_128IsAccepted_129IsRejected() // US-BANK-B128 / US-BANK-B129
    {
        string at128 = "Routing Number: 120000003" + new string(' ', 103) + "Account Number: AB12CD34";
        string at129 = "Routing Number: 120000003" + new string(' ', 104) + "Account Number: AB12CD34";

        Assert.Equal(152, at128.Length);
        Assert.Equal(128, at128.IndexOf("Account", StringComparison.Ordinal));
        Assert.Equal("us.bank_account|BankAccount|L3|S+C+P|144:8|AB12CD34|[계좌1]|140", Oracle(Assert.Single(Bank(at128))));
        Assert.Equal(153, at129.Length);
        Assert.Empty(Bank(at129));
    }

    [Theory]
    [InlineData("Routing Number: 120000003")]                                              // US-BANK-FN01: routing alone
    [InlineData("Account Number: AB12CD34")]                                               // US-BANK-FN02: generic unpaired
    [InlineData("AB12CD34")]                                                               // US-BANK-FN03: bare token
    [InlineData("Routing Number: 120000004 | Account Number: AB12CD34")]                   // BADCHK
    [InlineData("Routing Number: 130000002 | Account Number: AB12CD34")]                   // BADPFX
    [InlineData("Routing Number: 120000003 | Email: a@b.com | Account Number: AB12CD34")]  // 3FIELD
    [InlineData("Routing Number: 120000003\n\nAccount Number: AB12CD34")]                  // BLANK
    [InlineData("Bank Account: ABC")]                                                      // NEG-LEN3
    [InlineData("Bank Account: ABCDEFGHIJKLMNOPQR")]                                       // NEG-LEN18
    [InlineData("Bank Account: AB12-CD34")]                                                // NEG-HYPHEN
    [InlineData("Routing Number: 12000000" + Fullwidth3 + " | Account Number: AB12CD34")]  // NEG-UNICODE
    [InlineData("Order Number: AB12CD34")]                                                 // ORDER-N01
    [InlineData("Customer Account: AB12CD34")]                                             // CUSTOMER-N01
    public void Bank_FrozenNegatives_ProduceNoCandidate(string raw) => Assert.Empty(Bank(raw));

    // ===== BANK: ABA ========================================================================================================

    [Theory]
    [InlineData("00")] [InlineData("01")] [InlineData("05")] [InlineData("12")]
    [InlineData("21")] [InlineData("26")] [InlineData("32")]
    [InlineData("61")] [InlineData("66")] [InlineData("72")]
    [InlineData("80")]
    public void Aba_AcceptedPrefixes_WithAValidChecksum_PairTheGenericAccount(string prefix) // synthetic
    {
        string routing = Aba(prefix + "000000");
        string raw = $"Routing Number: {routing} | Account Number: AB12CD34";

        Assert.Single(Bank(raw));
    }

    [Theory]
    [InlineData("13")] [InlineData("20")] [InlineData("33")] [InlineData("60")] [InlineData("73")]
    [InlineData("79")] [InlineData("81")] [InlineData("99")]
    public void Aba_OtherPrefixes_NeverProvidePairingEvidence_EvenWithAValidChecksum(string prefix) // synthetic
    {
        string routing = Aba(prefix + "000000");
        string raw = $"Routing Number: {routing} | Account Number: AB12CD34";

        Assert.Empty(Bank(raw));
    }

    [Fact]
    public void Aba_EveryWrongCheckDigit_IsRejected_ExactlyOneOfTenIsValid() // synthetic
    {
        int accepted = 0;
        for (int digit = 0; digit < 10; digit++)
        {
            string raw = $"Routing Number: 12000000{digit} | Account Number: AB12CD34";
            accepted += Bank(raw).Count;
        }

        Assert.Equal(1, accepted);
    }

    [Theory]
    [InlineData("Routing Number: 12000000 | Account Number: AB12CD34")]       // 8 digits
    [InlineData("Routing Number: 1200000030 | Account Number: AB12CD34")]     // 10 digits
    [InlineData("Routing Number: 12000 0003 | Account Number: AB12CD34")]     // a space inside
    [InlineData("Routing Number: 120-000-003 | Account Number: AB12CD34")]
    [InlineData("Routing Number: 12000000A | Account Number: AB12CD34")]
    [InlineData("Routing No: 120000003 | Account Number: AB12CD34")]          // not an approved routing label
    [InlineData("Routing: 120000003 | Account Number: AB12CD34")]
    [InlineData("Transit Number: 120000003 | Account Number: AB12CD34")]
    public void Aba_MalformedOrUnapprovedRouting_IsNoPairing(string raw) => Assert.Empty(Bank(raw)); // synthetic

    [Fact]
    public void Aba_ARoutingNumberAlone_NeverMakesACandidate_InAnyDetector()
    {
        foreach (string raw in new[] { "Routing Number: 120000003", "ABA Routing Number: 120000003", "ABA Number: 120000003", "Routing Transit Number: 120000003" })
        {
            Assert.DoesNotContain(Pipe(raw), c => c.PiiType == PiiType.BankAccount);
            Assert.Empty(Bank(raw));
        }
    }

    // ===== BANK: pairing ====================================================================================================

    [Theory]
    [InlineData("Routing Number: 120000003; Account Number: AB12CD34", 43)]
    [InlineData("Routing Number: 120000003\tAccount Number: AB12CD34", 42)]
    [InlineData("Routing Number: 120000003 Account Number: AB12CD34", 42)]
    [InlineData("Routing Number: 120000003, Account Number: AB12CD34", 43)]
    [InlineData("Routing Number: 120000003\r\nAccount Number: AB12CD34", 43)]
    [InlineData("Routing Number: 120000003\rAccount Number: AB12CD34", 42)]
    public void Bank_TheFinancialFieldSeparators_AreSpacesCommaSemicolonTabPipeAndTheLineBreak(string raw, int accountStart) // synthetic
    {
        var candidate = Assert.Single(Bank(raw));

        Assert.Equal(new RawSpan(accountStart, 8), candidate.Span);
        Assert.Equal("AB12CD34", Slice(raw, candidate));
        Assert.Equal(ResolutionEvidence.SCJP, candidate.Evidence);
    }

    [Theory]
    [InlineData("Account Number: AB12CD34, Routing Number: 120000003", 16)]
    [InlineData("Account Number: AB12CD34; Routing Number: 120000003", 16)]
    [InlineData("Account Number: AB12CD34\r\nRouting Number: 120000003", 16)]
    public void Bank_TheOrderDoesNotMatter_AndACommaIsNeverPartOfTheValue(string raw, int accountStart) // synthetic
    {
        var candidate = Assert.Single(Bank(raw));

        Assert.Equal(new RawSpan(accountStart, 8), candidate.Span);
    }

    [Theory]
    [InlineData("Routing Number: 120000003\nAccount Number: AB12CD34\nName: Jo")]            // a third line after the pair is fine
    [InlineData("Name: Jo\nRouting Number: 120000003\nAccount Number: AB12CD34")]            // and so is one before
    public void Bank_ARecognisedFieldOutsideThePair_DoesNotBreakIt(string raw) => Assert.Single(Bank(raw)); // synthetic

    [Theory]
    [InlineData("Routing Number: 120000003\nName: Jo\nAccount Number: AB12CD34")]             // a third (nonfinancial) line between
    [InlineData("Routing Number: 120000003\n \nAccount Number: AB12CD34")]                    // a blank-looking line between
    [InlineData("Routing Number: 120000003\n\n\nAccount Number: AB12CD34")]
    [InlineData("Routing Number: 120000003 | Phone: 555 | Account Number: AB12CD34")]        // a nonfinancial field between, same line
    [InlineData("Routing Number: 120000003\nPhone: 555 Account Number: AB12CD34")]            // the second line holds two recognised fields
    [InlineData("Routing Number: 120000003 Account Number: 5 Account Number: AB12CD34")]      // a financial field in between
    public void Bank_AnInterveningLineOrField_BreaksThePair(string raw) => Assert.Empty(Bank(raw)); // synthetic

    [Fact]
    public void Bank_TwoLineForm_NeedsExactlyOneFinancialFieldPerLine() // synthetic
    {
        // The first line already holds a routing AND an account field: the two-line form (one financial field per line) does not
        // apply, and the pair on the first line is judged on its own line.
        const string raw = "Routing Number: 120000003 | Account Number: AB12CD34\nRouting Number: 120000003";

        var found = Bank(raw);

        Assert.Equal(new RawSpan(44, 8), Assert.Single(found).Span);
    }

    [Fact]
    public void Bank_AnAccountIsPairedWithItsOwnNeighbour_NotWithAFarAwayRouting() // synthetic
    {
        string raw = "Routing Number: 120000003\nPhone: 555\nChecking Account: ZZ99YY88\nAccount Number: AB12CD34";

        var found = Bank(raw);

        // the checking account is independent (C); the generic account has no routing neighbour -> only the checking account
        Assert.Equal(["ZZ99YY88"], found.Select(c => Slice(raw, c)));
        Assert.Equal(ResolutionEvidence.C, found[0].Evidence);
    }

    [Fact]
    public void Bank_ABankSpecificAccountWithAValidRoutingNeighbour_IsStrongerEvidence_AmbiguityLeftUnresolved() // [EA_UNRESOLVED]
    {
        // D2.2 allows a bank-specific label to stand alone but does not say whether a valid routing neighbour upgrades the
        // evidence. Pairing is read as the strongest evidence (S+C+P); the protected span is unchanged either way.
        const string alone = "Bank Account: AB12CD34";
        const string paired = "Routing Number: 120000003 | Bank Account: AB12CD34";

        Assert.Equal(ResolutionEvidence.C, Assert.Single(Bank(alone)).Evidence);
        var candidate = Assert.Single(Bank(paired));
        Assert.Equal(ResolutionEvidence.SCJP, candidate.Evidence);
        Assert.Equal("AB12CD34", Slice(paired, candidate));
    }

    [Fact]
    public void Bank_ABankSpecificAccountWithAnInvalidRouting_StaysIndependent() // synthetic
    {
        string raw = "Routing Number: 120000004 | Bank Account: AB12CD34";

        var candidate = Assert.Single(Bank(raw));

        Assert.Equal(ResolutionEvidence.C, candidate.Evidence);
    }

    [Fact]
    public void Bank_OnlyTheAccountSpanIsProtected_TheRoutingIsNeverACandidate_Anywhere()
    {
        const string raw = "Routing Number: 120000003 | Account Number: AB12CD34";

        var candidate = Assert.Single(Pipe(raw));

        Assert.Equal("us.bank_account", candidate.DetectorId.Value);
        Assert.Equal("AB12CD34", Slice(raw, candidate));
        Assert.DoesNotContain(Pipe(raw), c => Slice(raw, c) == Routing);
    }

    [Theory]
    [InlineData("Bank Account: ABCD", 4)]
    [InlineData("Bank Account: 1234", 4)]
    [InlineData("Bank Account: ABCDEFGHIJKLMNOPQ", 17)]
    [InlineData("bank account = ab12cd34", 8)]                   // [EA] lower-case label and value
    [InlineData("Checking Account # 123456789012", 12)]
    public void Bank_Accepts4To17AsciiAlphanumerics(string raw, int length) // synthetic
    {
        Assert.Equal(length, Assert.Single(Bank(raw)).Span.Length);
    }

    [Theory]
    [InlineData("Bank Account: AB12 CD34")]                      // a space inside
    [InlineData("Bank Account: AB12_CD34")]
    [InlineData("Bank Account: AB12.CD34")]
    [InlineData("Bank Account: ＡB12CD34")]                  // fullwidth A
    [InlineData("Bank Account: AB12CD3４")]
    [InlineData("Bank Accounts: AB12CD34")]                      // the label sits inside a longer word
    [InlineData("Bank Acct: AB12CD34")]                          // not an approved label
    [InlineData("Account: AB12CD34")]
    [InlineData("Checking: AB12CD34")]
    [InlineData("Savings Acct Number: AB12CD34")]
    [InlineData("Bank Account: AB12CD34 and more")]
    [InlineData("Bank Account:    AB12CD34")]
    [InlineData("Bank Account: ")]
    public void Bank_NearMissesAndUnapprovedForms_AreNoCandidate(string raw) => Assert.Empty(Bank(raw)); // synthetic

    [Fact]
    public void Bank_TheCanonicalValue_IsAsciiUpperCase() => // [EA] upper-case folding, as for the others
        Assert.Equal("AB12CD34", Assert.Single(Bank("bank account = ab12cd34")).Canonical!.Value.Value);

    [Fact]
    public void Bank_SpansAfterEmojiAreExactUtf16RawSpans_AndRepeatsAreSeparateCandidates()
    {
        const string prefix = "\U0001F600\U0001F4A9 ";
        string raw = prefix + "Routing Number: 120000003 | Account Number: AB12CD34\nBank Account: AB12CD34";

        var found = Bank(raw);

        Assert.Equal(2, found.Count);
        Assert.Equal(new RawSpan(prefix.Length + 44, 8), found[0].Span);
        Assert.Equal(found[0].Canonical, found[1].Canonical);
    }

    [Fact]
    public void Bank_TheNinetySixCharacterFieldLimit_AppliesToTheValueEnd_NotToTrailingSpaces() // reading required by US-BANK-B128
    {
        // US-BANK-B128 keeps the routing field valid with 103 trailing spaces, so for the financial fields the limit is measured
        // from the label start through the END OF THE VALUE (the shared FieldBinder counts the trailing spaces; see the report).
        Assert.Single(Bank("Bank Account: AB12CD34" + new string(' ', 200)));
        Assert.Empty(Bank("Bank Account: AB12CD34" + new string('A', 200)));
    }

    // ===== glued labels / the unresolved FieldBinder cases: behaviour characterised, not normalised =========================

    [Fact]
    public void Glued_AnExplicitSeparator_EndsTheFinancialFieldAndTheLicenseField_AsInTheOtherDetectors()
    {
        var dl = Assert.Single(Dl("Driver License: D1234567Phone: 5 USA"));
        Assert.Equal(new RawSpan(16, 8), dl.Span);

        // A glued "State: CA" is NOT jurisdiction evidence: the J label would sit inside a token (the same word rule as for the
        // passport). Unresolved glued-label ambiguity, reported rather than promoted.
        Assert.Empty(Dl("Driver License: D1234567State: CA"));
    }

    [Fact]
    public void Glued_AStrongLabelDirectlyAfterAToken_IsNotALabel_TheExistingConventionOfTheOtherDetectors()
    {
        // The strong "Account Number" label sits right after the routing digits: like every other detector's strong label inside
        // a longer token it is not recognised, so there is no account field and no pair. Unresolved glued-label ambiguity; not
        // promoted to a rule.
        Assert.Empty(Bank("Routing Number: 120000003Account Number: AB12CD34"));
    }

    [Fact]
    public void Glued_WithoutASeparator_TheLabelStaysInsideTheValue_UnresolvedAmbiguityPreserved()
    {
        // "Account Number" directly after the routing value WITHOUT ':' '#' '=' is not a field start (unchanged FieldBinder
        // behaviour): the routing value is then not a 9-digit token and there is no pair.
        Assert.Empty(Bank("Routing Number: 120000003Account Number AB12CD34"));
    }

    [Fact]
    public void Glued_HotelTail_IsReadAsAGluedLabel_CharacterizationOnly()
    {
        // "tel:" at the end of "Hotel:" is the approved label Tel after a token: the earlier value "D1234567Ho" (10) is complete.
        var candidate = Assert.Single(Dl("Driver License: D1234567Hotel: State: CA"));

        Assert.Equal(new RawSpan(16, 10), candidate.Span);
    }

    // ===== independence and resolver priority ===============================================================================

    [Fact]
    public void EachDetector_ReportsOnlyItsOwnIdentifier_OnAMixedText()
    {
        const string raw = "Driver License: D1234567 State: CA\nRouting Number: 120000003 | Account Number: AB12CD34";

        Assert.Equal(["us.driver_state_id"], Dl(raw).Select(c => c.DetectorId.Value));
        Assert.Equal(["us.bank_account"], Bank(raw).Select(c => c.DetectorId.Value));
        Assert.Equal(["us.driver_state_id", "us.bank_account"], Pipe(raw).OrderBy(c => c.Span.Start).Select(c => c.DetectorId.Value));
    }

    [Fact]
    public void AnAccountThatIsAlsoAValidBareMbi_IsOneMbi_BecauseS_BeatsC()
    {
        var winner = Assert.Single(Pipe("Bank Account: 1A23C45DE67"));

        Assert.Equal("us.mbi", winner.DetectorId.Value);
    }

    [Fact]
    public void AnAccountThatIsAlsoAValidBareSevis_IsOneSevis_BecauseS_BeatsC()
    {
        Assert.Equal("us.sevis", Assert.Single(Pipe("Bank Account: N1234567890")).DetectorId.Value);
    }

    [Fact]
    public void ALicenseValueThatIsAlsoACurrentFormatPassportToken_IsOneLicense_BecauseS_Plus_C_Plus_J_Wins()
    {
        // "A12345678" is a license value (label + J: S+C+J) and a passport route-A token (J: S+J) on the same span.
        const string raw = "Driver License: A12345678 State: NY";

        var winner = Assert.Single(Pipe(raw));

        Assert.Equal("us.driver_state_id", winner.DetectorId.Value);
        Assert.Equal(ResolutionEvidence.SCJP, winner.Evidence);
    }

    [Fact]
    public void ALicenseValueThatLooksLikeAnSsn_IsOneLicense_ByTheFrozenEvidenceOrder()
    {
        const string raw = "Driver License: 912-93-4567 State: CA";

        var winner = Assert.Single(Pipe(raw));

        Assert.Equal("us.driver_state_id", winner.DetectorId.Value);
        Assert.Equal("912934567", winner.Canonical!.Value.Value);
    }

    [Fact]
    public void TheResolution_DoesNotDependOnTheOrderOfTheDetectorsInTheFactory()
    {
        var forward = UsOptInPipeline.Create();
        var reversed = UsOptInPipeline.Create(usDetectorFactory: () => UsIntegrationCandidates.CreateDetectors().Reverse().ToList());
        const string raw = TenText + "\nBank Account: 1A23C45DE67\nDriver License: A12345678 State: NY";

        Assert.Equal(
            forward.Detect(raw).Candidates.OrderBy(c => c.Span.Start).Select(c => (c.DetectorId.Value, c.Span)),
            reversed.Detect(raw).Candidates.OrderBy(c => c.Span.Start).Select(c => (c.DetectorId.Value, c.Span)));
    }

    // ===== ten types in one pipeline, H-2 and the country scope ============================================================

    private const string TenText =
        "Passport Number: P12345678 State: NY\nA-Number: A1234567\nI-94 Number: 123456789AB\n" +
        "ATIN: 912-93-4567\nITIN: 912-70-1234\nSSN: 321-54-9876\nMBI: 1A23C45DE67\nSEVIS: N1234567890\n" +
        "Driver License Number: D123-4567 State: CA\nRouting Number: 120000003 | Account Number: AB12CD34";

    public static TheoryData<CountrySettingObservation> NotAuthenticatedOff => new()
    {
        CountrySettingObservation.AuthenticatedOn, CountrySettingObservation.Missing, CountrySettingObservation.Corrupt,
        CountrySettingObservation.Unauthenticated, CountrySettingObservation.UnknownVersion,
    };

    [Fact]
    public void ThePipeline_ProducesTheTenRecords_ThroughTheProductsOwnConstructor()
    {
        static string At(string value) => TenText.IndexOf(value, StringComparison.Ordinal).ToString();

        var found = Pipe(TenText).OrderBy(c => c.Span.Start).Select(OracleWithOwner).ToList();

        Assert.Equal(
            [
                $"us.passport|UsPassport|L3|S+C+J|{At("P12345678")}:9|P12345678|[여권번호1]|100|ownerClaim=true",
                $"us.a_number|UsANumber|L3|S+C|{At("A1234567")}:8|A1234567|[A-NUMBER1]|110",
                $"us.i94|UsI94|L3|S+C|{At("123456789AB")}:11|123456789AB|[I-941]|120",
                $"us.atin|UsAtin|L3|S+C|{At("912-93-4567")}:11|912934567|[ATIN1]|50",
                $"us.itin|UsItin|L3|S+C|{At("912-70-1234")}:11|912701234|[ITIN1]|60",
                $"us.ssn|UsSsn|L3|S+C|{At("321-54-9876")}:11|321549876|[SSN1]|70",
                $"us.mbi|UsMbi|L3|S+C|{At("1A23C45DE67")}:11|1A23C45DE67|[MBI1]|80",
                $"us.sevis|UsSevis|L3|S+C|{At("N1234567890")}:11|N1234567890|[SEVIS1]|90",
                $"us.driver_state_id|UsDriverStateId|L3|S+C+J|{At("D123-4567")}:9|D1234567|[운전면허번호1]|130",
                $"us.bank_account|BankAccount|L3|S+C+P|{At("AB12CD34")}:8|AB12CD34|[계좌1]|140",
            ],
            found);
    }

    [Fact]
    public void TheFiveAndThreeEarlierTypes_ProduceTheSameCandidates_InTheTenDetectorPipeline()
    {
        const string eightText =
            "Passport Number: P12345678 State: NY\nA-Number: A1234567\nI-94 Number: 123456789AB\n" +
            "ATIN: 912-93-4567\nITIN: 912-70-1234\nSSN: 321-54-9876\nMBI: 1A23C45DE67\nSEVIS: N1234567890";
        var eight = new DetectionPipeline(DetectorRegistry.Create(AllEight()), new UsTaxpayerFamilyArbiterProvider(), [new UsTaxpayerFamilyAttempts()]);
        var ten = UsOptInPipeline.Create();

        static List<string> Keys(DetectionResult r) =>
            r.Candidates.Select(c => $"{c.DetectorId.Value}|{c.Span}|{c.Canonical?.Value}|{c.Evidence}|{c.OwnerClaim}").OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Equal(Keys(eight.Detect(eightText)), Keys(ten.Detect(eightText)));
    }

    [Fact]
    public void TheApprovedIdList_CarriesTheTwoLiteralsOfThisTask_AmongTheEleven()
    {
        Assert.Contains(UsIntegrationCandidates.ApprovedDetectorIds, i => i.Value == "us.driver_state_id");
        Assert.Contains(UsIntegrationCandidates.ApprovedDetectorIds, i => i.Value == "us.bank_account");
        Assert.Equal(11, UsIntegrationCandidates.ApprovedDetectorIds.Count);
        Assert.Equal(11, UsOptInPipeline.UsScope(CountrySettingObservation.Missing).RequiredDetectorIds.Count);
    }

    [Theory]
    [InlineData("us.driver_state_id")]
    [InlineData("us.bank_account")]
    public void AFactoryThatDropsANewDetector_IsDetected_WheneverTheUsPackIsNotAuthenticatedOff(string dropped)
    {
        IReadOnlyList<IDetector> Without() => UsIntegrationCandidates.CreateDetectors().Where(d => d.Id.Value != dropped).ToList();

        foreach (var setting in new[]
                 { CountrySettingObservation.AuthenticatedOn, CountrySettingObservation.Missing, CountrySettingObservation.Corrupt,
                   CountrySettingObservation.Unauthenticated, CountrySettingObservation.UnknownVersion })
        {
            var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(() =>
                UsOptInPipeline.Create(unitedStatesSetting: setting, usDetectorFactory: Without).Detect(TenText));
            Assert.Equal(dropped, ex.Id.Value);
            Assert.Equal(RequiredDetectorReason.Unregistered, ex.Reason);
        }

        Assert.Empty(UsOptInPipeline.Create(unitedStatesSetting: CountrySettingObservation.AuthenticatedOff, usDetectorFactory: Without)
            .Detect(TenText).Candidates);
    }

    [Fact]
    public void ANewDetectorThatIsNotReady_IsDetected_WheneverTheUsPackIsNotAuthenticatedOff()
    {
        IReadOnlyList<IDetector> NotReadyBank() =>
        [
            .. UsIntegrationCandidates.CreateDetectors().Where(d => d.Id.Value != "us.bank_account"),
            new NotReadyDetector(new UsBankAccountCandidateDetector()),
        ];

        var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(() =>
            UsOptInPipeline.Create(unitedStatesSetting: CountrySettingObservation.Missing, usDetectorFactory: NotReadyBank).Detect(TenText));

        Assert.Equal("us.bank_account", ex.Id.Value);
        Assert.Equal(RequiredDetectorReason.NotReady, ex.Reason);
    }

    private sealed class NotReadyDetector(IDetector inner) : IDetector
    {
        public string Name => inner.Name;
        public PiiType PiiType => inner.PiiType;
        public DetectorId Id => inner.Id;
        public int ResolutionRank => inner.ResolutionRank;
        public DetectorReadinessState Readiness => DetectorReadinessState.UnavailableAuthoritySnapshot;
        public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context) => inner.Detect(context);
    }

    [Fact]
    public void UnitedStatesAuthenticatedOff_StopsAllTen_AndLeavesKoreanAndGlobalUntouched()
    {
        var legacy = new IDetector[]
        {
            new Privon.Detection.Detectors.PhoneDetector(), new Privon.Detection.Detectors.EmailDetector(),
            new Privon.Detection.Detectors.ResidentRegistrationNumberDetector(), new Privon.Detection.Detectors.CardNumberDetector(),
            new Privon.Detection.Detectors.BankAccountNumberDetector(), new Privon.Detection.Detectors.SecretDetector(),
            new Privon.Detection.Detectors.IpAddressDetector(), new Privon.Detection.Detectors.MacAddressDetector(),
            new Privon.Detection.Detectors.GpsCoordinateDetector(),
        };
        var shipped = DetectionPipeline.CreateDefault();
        var off = UsOptInPipeline.Create(legacy, unitedStatesSetting: CountrySettingObservation.AuthenticatedOff);
        string mixed = TenText + "\n연락처 010-1234-5678 이메일 user@example.test 901231-1234567";

        Assert.Empty(off.Detect(TenText).Candidates);
        Assert.Equal(
            shipped.Detect(mixed).Candidates.OrderBy(c => c.Span.Start).Select(c => (c.PiiType, c.Span)),
            off.Detect(mixed).Candidates.OrderBy(c => c.Span.Start).Select(c => (c.PiiType, c.Span)));
        Assert.True(shipped.Detect(mixed).Candidates.Count >= 3);
    }

    [Theory]
    [MemberData(nameof(NotAuthenticatedOff))]
    public void EveryOtherSetting_ProtectsAllTen_ExactlyLikeAnUnscopedPipeline(CountrySettingObservation setting)
    {
        var unscoped = UsOptInPipeline.Create();
        var scoped = UsOptInPipeline.Create(unitedStatesSetting: setting);

        Assert.Equal(10, scoped.Detect(TenText).Candidates.Count);
        Assert.Equal(
            unscoped.Detect(TenText).Candidates.Select(c => (c.DetectorId.Value, c.Span)).OrderBy(x => x.Span.Start),
            scoped.Detect(TenText).Candidates.Select(c => (c.DetectorId.Value, c.Span)).OrderBy(x => x.Span.Start));
    }

    [Fact]
    public void TheShippedDefaultPipeline_DetectsNeitherNewType()
    {
        var shipped = DetectionPipeline.CreateDefault();

        foreach (string raw in new[] { "Driver License Number: D123-4567 State: CA", "Routing Number: 120000003 | Account Number: AB12CD34", "Bank Account: AB12CD34" })
        {
            Assert.DoesNotContain(shipped.Detect(raw).Candidates, c => c.PiiType is PiiType.UsDriverStateId or PiiType.BankAccount);
        }
    }

    // ===== Level 3 policy and aliases (the real stages) ====================================================================

    [Theory]
    [InlineData("Driver License Number: D123-4567 State: CA")]
    [InlineData("Document Type: State ID; Customer ID Number: D1234567; State: CA")]
    [InlineData("Bank Account: AB12CD34")]
    [InlineData("Routing Number: 120000003 | Account Number: AB12CD34")]
    public void CurrentProductPolicy_IsNeedsDecision_ForEveryNewLevel3_AndGivesItNoAliasYet(string raw)
    {
        var candidate = Assert.Single(Pipe(raw));
        var evaluated = new EvaluatedCandidate(candidate, TrustState.Unknown);

        Assert.Equal(RiskLevel.Level3, candidate.RiskLevel);
        Assert.Equal(CandidateDisposition.NeedsDecision, CandidatePolicyEvaluator.Decide(evaluated));
        Assert.Null(AliasAssigner.Assign(CandidatePolicyEvaluator.Evaluate([evaluated]), new AliasMap()).Single().Alias);
    }

    [Fact]
    public void TheAliasMap_NumbersEqualValuesTogether_AndDifferentValuesSeparately_ForTheNewTypes()
    {
        var map = new AliasMap();
        const string raw =
            "DLN: D1234567 State: CA\nDLN: d123-4567 State: CA\nDLN: Z7654321 State: CA\n" +
            "Bank Account: AB12CD34\nBank Account: ab12cd34\nBank Account: ZZ99YY88";

        var tokens = Pipe(raw).OrderBy(c => c.Span.Start).Select(c => map.GetOrAdd(c.Canonical!.Value).Value).ToList();

        Assert.Equal(["[운전면허번호1]", "[운전면허번호1]", "[운전면허번호2]", "[계좌1]", "[계좌1]", "[계좌2]"], tokens);
    }

    // ===== no raw value in any diagnostic string ===========================================================================

    [Fact]
    public void NoDiagnosticString_ShowsTheNewValues()
    {
        string[] secrets = ["D1234567", "D123-4567", "AB12CD34", "120000003"];
        var result = UsOptInPipeline.Create().Detect(TenText);
        var strings = new List<string> { result.ToString() ?? string.Empty };
        strings.AddRange(result.Candidates.Select(c => c.ToString()));
        strings.AddRange(result.Candidates.Select(c => c.Canonical!.Value.ToString()));
        strings.AddRange(UsIntegrationCandidates.CreateDetectors().Select(d => d.ToString() ?? string.Empty));

        foreach (string text in strings)
        {
            foreach (string secret in secrets)
            {
                Assert.DoesNotContain(secret, text);
            }
        }

        var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(() =>
            UsOptInPipeline.Create(
                unitedStatesSetting: CountrySettingObservation.Missing,
                usDetectorFactory: () => UsIntegrationCandidates.CreateDetectors().Where(d => d.Id.Value != "us.bank_account").ToList())
            .Detect(TenText));
        foreach (string secret in secrets)
        {
            Assert.DoesNotContain(secret, ex.Message);
            Assert.DoesNotContain(secret, ex.ToString());
        }
    }

    [Fact]
    public void TheInternalMatchRecord_NeverPrintsTheValue_ForTheNewTypes()
    {
        var assembly = typeof(UsPassportCandidateDetector).Assembly;
        var matchType = assembly.GetType("Privon.UsProductIntegration.UsDocumentMatch", throwOnError: true)!;
        var evidenceType = assembly.GetType("Privon.UsProductIntegration.UsDocumentEvidence", throwOnError: true)!;
        var match = Activator.CreateInstance(
            matchType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            binder: null, args: [new RawSpan(0, 8), Enum.ToObject(evidenceType, 7), "AB12CD34", OwnerClaim.NotApplicable, RiskLevel.Level3, DetectionConfidence.High], culture: null)!;

        Assert.DoesNotContain("AB12CD34", match.ToString()!);
    }

    // ===== bounded work =====================================================================================================

    [Fact]
    public void ManyRepeatedFields_AndLongLines_AreBounded()
    {
        string many = string.Join("\n", Enumerable.Repeat("Routing Number: 120000003 | Account Number: AB12CD34", 3_000));
        string manyDl = string.Join("\n", Enumerable.Repeat("DLN: D1234567 State: CA", 3_000));
        string longLine = "Bank Account: " + new string('x', 300_000) + " Routing Number: " + new string('9', 300_000);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(3_000, Bank(many).Count);
        Assert.Equal(3_000, Dl(manyDl).Count);
        Assert.Empty(Bank(longLine));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "detection must stay near-linear");
    }
}
