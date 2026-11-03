using Privon.Core;
using Privon.Detection;
using Privon.UsProductIntegration;
using static Privon.UsProductIntegration.Tests.UsProductHarness;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY. us.phone (310), us.address (330) and us.name (350) through the REAL IDetector / DetectionCandidate contracts.
// Phone and address run against the four-entry D3A test authority served through the production loader. Frozen D3A rows are reused
// with their expected values UNCHANGED (row ids in comments); everything else is marked "synthetic".
//
// ADDRESS CANONICAL. The frozen D3A manifest defines the oracle's canonical column as "canonical value WHEN DEFINED", and writes `null`
// for an address: the contract does not define one. At runtime the product cannot alias an ABSENT canonical (AliasAssigner registers
// no alias, AliasReplacer then throws), so the detector stamps the matched text. The address checks below therefore compare every field
// the oracle DOES define -- one by one, never a record string with the canonical column substituted -- and assert the stamped canonical
// separately (see UsAddressCanonicalContractTests for the evidence and the stability / privacy properties).
public class UsPhoneAddressNameTests
{
    private static readonly UsAuthoritySet Authority = UsTestAuthorityProvider.ValidSet();

    private static IReadOnlyList<DetectionCandidate> Phone(string raw) => Run(new UsPhoneCandidateDetector(Authority), raw);

    private static IReadOnlyList<DetectionCandidate> Address(string raw) => Run(new UsAddressCandidateDetector(Authority), raw);

    private static IReadOnlyList<DetectionCandidate> Name(string raw) => Run(new UsNameCandidateDetector(), raw);

    /// <summary>
    /// The fields of the frozen address record that the oracle DEFINES (id, type, level, evidence, span, alias, rank). The canonical column
    /// is not part of this string: it is checked on its own, as a PRESENT value, never rewritten to match the oracle's `null`.
    /// </summary>
    private static string AddressRecord(DetectionCandidate c)
    {
        string[] parts = Oracle(c).Split('|');
        return string.Join('|', parts[0], parts[1], parts[2], parts[3], parts[4], parts[6], parts[7]);
    }

    // ===== identity, readiness ============================================================================================

    [Fact]
    public void TheThreeDetectors_CarryTheFrozenIdentity_Rank_Type()
    {
        (IDetector Detector, string Id, int Rank, PiiType Type)[] expected =
        [
            (new UsPhoneCandidateDetector(Authority), "us.phone", 310, PiiType.Phone),
            (new UsAddressCandidateDetector(Authority), "us.address", 330, PiiType.Address),
            (new UsNameCandidateDetector(), "us.name", 350, PiiType.Name),
        ];

        foreach (var (detector, id, rank, type) in expected)
        {
            Assert.Equal(id, detector.Id.Value);
            Assert.Equal(rank, detector.ResolutionRank);
            Assert.Equal(type, detector.PiiType);
            Assert.Equal(DetectorReadinessState.Ready, detector.Readiness);
            Assert.Equal(rank, DetectorRegistry.Create([detector]).GetRank(detector.Id));
        }
    }

    [Fact]
    public void WithoutAnAuthority_PhoneAndAddressAreUnavailable_AndEmitNothing_ButNameIsReady()
    {
        var phone = new UsPhoneCandidateDetector();
        var address = new UsAddressCandidateDetector();

        Assert.Equal(DetectorReadinessState.UnavailableAuthoritySnapshot, phone.Readiness);
        Assert.Equal(DetectorReadinessState.UnavailableAuthoritySnapshot, address.Readiness);
        Assert.Equal(DetectorReadinessState.Ready, new UsNameCandidateDetector().Readiness);
        Assert.Empty(Run(phone, "Phone: 212-555-1212 +12125551212"));
        Assert.Empty(Run(address, "Address: 123 Main ST\n123 Main ST, New York, NY 10001"));
    }

    [Fact]
    public void ADetector_RefusesANullContext_AndRunsOnEmptyText()
    {
        foreach (IDetector d in new IDetector[]
                 { new UsPhoneCandidateDetector(Authority), new UsAddressCandidateDetector(Authority), new UsNameCandidateDetector() })
        {
            Assert.Throws<ArgumentNullException>(() => d.Detect(null!));
            Assert.Empty(Run(d, string.Empty));
            Assert.Empty(Run(d, "오늘 날씨가 좋네요"));
        }

        Assert.Throws<ArgumentNullException>(() => new UsPhoneCandidateDetector(null!));
        Assert.Throws<ArgumentNullException>(() => new UsAddressCandidateDetector(null!));
    }

    // ===== PHONE: frozen D3A rows ===========================================================================================

    [Theory]
    [InlineData("212-555-1212", "us.phone|Phone|L2|S|0:12|2125551212|[전화번호1]|310")]                    // P01
    [InlineData("+12125551212", "us.phone|Phone|L2|S|0:12|2125551212|[전화번호1]|310")]                    // P02
    [InlineData("212.555.1212", "us.phone|Phone|L2|S|0:12|2125551212|[전화번호1]|310")]                    // FMT-DOT
    [InlineData("(212) 555-1212", "us.phone|Phone|L2|S|0:14|2125551212|[전화번호1]|310")]                  // FMT-PAREN
    [InlineData("212 555 1212", "us.phone|Phone|L2|S|0:12|2125551212|[전화번호1]|310")]                    // FMT-SPACE
    [InlineData("+1 212 555 1212", "us.phone|Phone|L2|S|0:15|2125551212|[전화번호1]|310")]                 // CC-SP
    [InlineData("+1-212-555-1212", "us.phone|Phone|L2|S|0:15|2125551212|[전화번호1]|310")]                 // CC-HYPHEN
    [InlineData("1-212-555-1212", "us.phone|Phone|L2|S|0:14|2125551212|[전화번호1]|310")]                  // CC-1HYPHEN
    [InlineData("+1 (212) 555-1212", "us.phone|Phone|L2|S|0:17|2125551212|[전화번호1]|310")]               // CC-PAREN
    [InlineData("1 (212) 555-1212", "us.phone|Phone|L2|S|0:16|2125551212|[전화번호1]|310")]                // CC-1PAREN
    [InlineData("Phone: 2125551212", "us.phone|Phone|L2|S+C|7:10|2125551212|[전화번호1]|310")]             // LBL01
    [InlineData("Phone Number: 2125551212", "us.phone|Phone|L2|S+C|14:10|2125551212|[전화번호1]|310")]     // LBL02
    [InlineData("Telephone: 2125551212", "us.phone|Phone|L2|S+C|11:10|2125551212|[전화번호1]|310")]        // LBL03
    [InlineData("Telephone Number: 2125551212", "us.phone|Phone|L2|S+C|18:10|2125551212|[전화번호1]|310")] // LBL04
    [InlineData("Tel: 2125551212", "us.phone|Phone|L2|S+C|5:10|2125551212|[전화번호1]|310")]               // LBL05
    [InlineData("Mobile: 2125551212", "us.phone|Phone|L2|S+C|8:10|2125551212|[전화번호1]|310")]            // LBL06
    [InlineData("Mobile Number: 2125551212", "us.phone|Phone|L2|S+C|15:10|2125551212|[전화번호1]|310")]    // LBL07
    [InlineData("Cell: 2125551212", "us.phone|Phone|L2|S+C|6:10|2125551212|[전화번호1]|310")]              // LBL08
    [InlineData("Cell Phone: 2125551212", "us.phone|Phone|L2|S+C|12:10|2125551212|[전화번호1]|310")]       // LBL09
    [InlineData("Cell Phone Number: 2125551212", "us.phone|Phone|L2|S+C|19:10|2125551212|[전화번호1]|310")] // LBL10
    [InlineData("Phone: 212-555-1212 x 42", "us.phone|Phone|L2|S+C|7:17|2125551212x42|[전화번호1]|310")]   // EXT01
    [InlineData("Phone: 212-555-1212 ext 42", "us.phone|Phone|L2|S+C|7:19|2125551212x42|[전화번호1]|310")] // EXT02 / P03
    [InlineData("Phone: 212-555-1212 extension 42", "us.phone|Phone|L2|S+C|7:25|2125551212x42|[전화번호1]|310")] // EXT04
    [InlineData("Phone: 212-555-1212 ext. 42", "us.phone|Phone|L2|S+C|7:12|2125551212|[전화번호1]|310")]  // EXT03: the tail is rejected
    [InlineData("Phone: 212-555-1212 ex 42", "us.phone|Phone|L2|S+C|7:12|2125551212|[전화번호1]|310")]    // EXT-N01
    public void Phone_FrozenPositives_MatchTheOracleRecordExactly(string raw, string expected) =>
        Assert.Equal(expected, Oracle(Assert.Single(Phone(raw))));

    [Theory]
    [InlineData("2125551212")]                         // FN01: unlabelled compact
    [InlineData("555-1212")]                           // FN02
    [InlineData("1-800-FLOWERS")]                      // FN03
    [InlineData("Cellular: 2125551212")]               // LBL-N01
    [InlineData("+1.212.555.1212")]                    // MIXED
    [InlineData("416-555-1212")]                       // N01: NPA not in the snapshot
    [InlineData("X212-555-1212Y")]                     // NEG-EMBED
    [InlineData("212-555-１２12")]             // NEG-UNICODE
    [InlineData("112-555-1212")]                       // NPA1
    [InlineData("212-155-1212")]                       // NXX1
    public void Phone_FrozenNegatives_ProduceNoCandidate(string raw) => Assert.Empty(Phone(raw));

    // ===== PHONE: synthetic ==================================================================================================

    [Theory]
    [InlineData("340-555-1212")]
    [InlineData("787-555-1212")]
    [InlineData("939-555-1212")]
    public void Phone_EveryNpaOfTheSnapshot_IsAccepted(string raw) => Assert.Single(Phone(raw)); // synthetic

    [Theory]
    [InlineData("213-555-1212")]     // valid shape, not in the snapshot
    [InlineData("800-555-1212")]
    [InlineData("911-555-1212")]
    [InlineData("0-555-1212")]
    [InlineData("212-055-1212")]
    [InlineData("212-955-121")]      // short
    [InlineData("212-555-12123")]    // long
    [InlineData("212 555-1212")]     // mixed separators
    [InlineData("212-555.1212")]
    [InlineData("(212)555-1212")]    // no space after the parenthesis
    [InlineData("(212) 555 1212")]
    [InlineData("(212)-555-1212")]
    [InlineData("+1212 555 1212")]
    [InlineData("++12125551212")]
    [InlineData("+1212555121")]
    [InlineData("Phone: 21255512")]
    [InlineData("Phone: abc")]
    public void Phone_NearMisses_AreNoCandidate(string raw) => Assert.Empty(Phone(raw)); // synthetic

    [Theory]
    [InlineData("+1 212-555-1212", 3)]   // not one of the six country-code forms: only the approved domestic grouping is a phone
    [InlineData("1 212 555 1212", 2)]
    [InlineData("+2 212 555 1212", 3)]
    public void Phone_AnUnlistedPrefix_StaysOutsideTheSpan_OnlyTheDomesticGroupingIsAPhone(string raw, int start) // synthetic
    {
        Assert.Equal(new RawSpan(start, 12), Assert.Single(Phone(raw)).Span);
    }

    [Theory]
    [InlineData("Phone: 212-555-1212 x42", 7, 16)]
    [InlineData("Phone: 212-555-1212x42", 7, 15)]
    [InlineData("Phone: 212-555-1212 EXT 7", 7, 18)]
    [InlineData("Phone: 212-555-1212 Extension 123456", 7, 29)]
    public void Phone_AnExtension_NeedsAnApprovedIntroducerAndDigits(string raw, int start, int length) // synthetic, [EA] case-insensitive
    {
        Assert.Equal(new RawSpan(start, length), Assert.Single(Phone(raw)).Span);
    }

    [Theory]
    [InlineData("Phone: 212-555-1212 x")]
    [InlineData("Phone: 212-555-1212 x 42abc")]
    [InlineData("Phone: 212-555-1212 xt 42")]
    [InlineData("Phone: 212-555-1212 extn 42")]
    [InlineData("Phone: 212-555-1212  x 42")]       // two spaces
    [InlineData("Phone: 212-555-1212 #42")]
    public void Phone_AnUnapprovedOrIncompleteExtension_StaysOutsideTheSpan(string raw) =>
        Assert.Equal(12, Assert.Single(Phone(raw)).Span.Length); // synthetic

    [Theory]
    [InlineData("Call 212-555-1212.", 5, 12)]
    [InlineData("(212-555-1212)", 1, 12)]
    [InlineData("a,212-555-1212;b", 2, 12)]
    [InlineData("Tel:212-555-1212", 4, 12)]
    public void Phone_SentencePunctuationAroundTheNumber_IsNotPartOfIt(string raw, int start, int length) // synthetic
    {
        Assert.Equal(new RawSpan(start, length), Assert.Single(Phone(raw)).Span);
    }

    [Theory]
    [InlineData("212-555-1212-5")]
    [InlineData("1212-555-1212")]
    [InlineData("212-555-12123456")]
    [InlineData("a212-555-1212")]
    [InlineData("212-555-1212a")]
    [InlineData("é212-555-1212")]    // a letter before the number
    [InlineData("Phone: 2125551212a")]
    [InlineData("Phone: 212555121")]
    public void Phone_TheTokenBoundaries_AreExact(string raw) => Assert.Empty(Phone(raw)); // synthetic

    [Fact]
    public void Phone_ALabelledValue_MustStartTheField_ButAFormattedNumberNeedsNoLabel() // synthetic
    {
        Assert.Empty(Phone("Phone: call 2125551212"));                                 // compact only at the value start
        Assert.Equal(new RawSpan(12, 12), Assert.Single(Phone("Phone: call 212-555-1212")).Span); // formatted anywhere
    }

    [Fact]
    public void Phone_Evidence_IsSForAFormatOnly_AndSPlusCWithAnApprovedLabel()
    {
        Assert.Equal(ResolutionEvidence.S, Assert.Single(Phone("212-555-1212")).Evidence);
        Assert.Equal(ResolutionEvidence.SC, Assert.Single(Phone("Tel: 212-555-1212")).Evidence);
        var c = Assert.Single(Phone("Tel: 212-555-1212"));
        Assert.Equal(RiskLevel.Level2, c.RiskLevel);
        Assert.Equal(DetectionConfidence.High, c.Confidence);
    }

    [Fact]
    public void Phone_SpansAfterEmoji_RepeatsAndTwoNumbers_AreExactRawSpans()
    {
        const string prefix = "\U0001F600\U0001F4A9 ";
        string raw = prefix + "Phone: 212-555-1212; Tel: 340.555.1212\n(787) 555-1212";

        var found = Phone(raw);

        Assert.Equal(3, found.Count);
        Assert.Equal(new RawSpan(prefix.Length + 7, 12), found[0].Span);
        Assert.Equal(["2125551212", "3405551212", "7875551212"], found.Select(c => c.Canonical!.Value.Value));
        Assert.Equal(found[0].Canonical, Phone("Phone: 212-555-1212")[0].Canonical);
    }

    [Fact]
    public void Phone_ABoundedWork_OnManyNumbersAndALongRun()
    {
        string many = string.Join("\n", Enumerable.Repeat("Tel: 212-555-1212 ext 7", 4_000));
        string digits = new string('2', 400_000) + " " + string.Concat(Enumerable.Repeat("212-55", 50_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(4_000, Phone(many).Count);
        Assert.Empty(Phone(digits));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    // ===== ADDRESS: frozen D3A rows ==========================================================================================

    [Theory]
    [InlineData("Address: 123 Main ST", "us.address|Address|L2|S+C|9:11|[주소1]|330")]                       // P01
    [InlineData("Address: 123 Main ST; memo", "us.address|Address|L2|S+C|9:11|[주소1]|330")]                // B01
    [InlineData("Address: 123 Main ST\rMemo", "us.address|Address|L2|S+C|9:11|[주소1]|330")]                // END-CR
    [InlineData("Address: 123 Main ST\tMemo", "us.address|Address|L2|S+C|9:11|[주소1]|330")]                // END-HT
    [InlineData("Address: 123 Main ST\nMemo", "us.address|Address|L2|S+C|9:11|[주소1]|330")]                // END-LF
    [InlineData("Address: 123 Main ST Phone: 2125551212", "us.address|Address|L2|S+C|9:11|[주소1]|330")]    // END-NEXTLABEL
    [InlineData("Address: HC 1 Box 22", "us.address|Address|L2|S+C|9:11|[주소1]|330")]                      // HC
    [InlineData("Address: RR 2 Box 15", "us.address|Address|L2|S+C|9:11|[주소1]|330")]                      // RR
    [InlineData("Home Address: 123 Main ST", "us.address|Address|L2|S+C|14:11|[주소1]|330")]                // LBL-HOME
    [InlineData("Residential Address: 123 Main ST", "us.address|Address|L2|S+C|21:11|[주소1]|330")]         // LBL-RES
    [InlineData("Street Address: 123 Main ST", "us.address|Address|L2|S+C|16:11|[주소1]|330")]              // LBL-STREET
    [InlineData("Mailing Address: PO Box 123", "us.address|Address|L2|S+C|17:10|[주소1]|330")]              // P03
    [InlineData("Address: 123 Main ST APT 4B", "us.address|Address|L2|S+C|9:18|[주소1]|330")]               // UNIT-APT
    [InlineData("Address: 123 Main ST STE 5", "us.address|Address|L2|S+C|9:17|[주소1]|330")]                // UNIT-STE
    [InlineData("Address: 123 Main ST UNIT 7", "us.address|Address|L2|S+C|9:18|[주소1]|330")]               // UNIT-UNIT
    [InlineData("123 Main ST, New York, NY 10001", "us.address|Address|L2|S+J|0:31|[주소1]|330")]           // P02
    [InlineData("123 Main ST, New York, NY 10001.", "us.address|Address|L2|S+J|0:31|[주소1]|330")]         // TRAILPUNCT
    [InlineData("123 Main ST, New York, NY 10001-1234", "us.address|Address|L2|S+J|0:36|[주소1]|330")]     // ZIP4
    public void Address_FrozenPositives_MatchEveryOracleFieldTheOracleDefines_AndTheCanonicalIsPresent(string raw, string expected)
    {
        var candidate = Assert.Single(Address(raw));

        Assert.Equal(expected, AddressRecord(candidate));
        Assert.NotNull(candidate.Canonical); // the product cannot alias an absent canonical: see the class comment
        Assert.Equal(raw.Substring(candidate.Span.Start, candidate.Span.Length), candidate.Canonical!.Value.Value);
    }

    [Fact]
    public void Address_TheNinetySixCharacterBoundary_96IsAccepted_97IsRejected() // MAX96 / MAX97
    {
        string at96 = "Address: 123 " + new string('A', 40) + " " + new string('B', 39) + " ST";
        string at97 = "Address: 123 " + new string('A', 40) + " " + new string('B', 40) + " ST";

        Assert.Equal(96, at96.Length);
        Assert.Equal(97, at97.Length);
        Assert.Equal("us.address|Address|L2|S+C|9:87|[주소1]|330", AddressRecord(Assert.Single(Address(at96))));
        Assert.Empty(Address(at97));
    }

    [Theory]
    [InlineData("ZIP: 10001")]                                          // FN01
    [InlineData("State: NY")]                                           // FN02
    [InlineData("Address: 123 Main")]                                   // N01
    [InlineData("123 One Two Three Four Five ST, New York, NY 10001")]  // NEG-FIVESTREET
    [InlineData("123 Main ST\nNew York, NY 10001")]                     // NEG-MULTILINE
    [InlineData("123 Main ST, New York, ZZ 10001")]                     // NEG-STATE
    [InlineData("Address: 123 Main ROADWAY")]                           // NEG-SUFFIX
    [InlineData("Address: １２23 Main ST")]                     // NEG-UNICODE
    [InlineData("123 Main ST, New York, NY 1000")]                      // NEG-ZIP
    [InlineData("123 Main ST,  New York, NY 10001")]                    // TWOSP
    [InlineData("APT 4B")]                                              // UNITONLY
    [InlineData("123 Main STNew York, NY 10001")]                       // ZEROWIDTH
    public void Address_FrozenNegatives_ProduceNoCandidate(string raw) => Assert.Empty(Address(raw));

    // ===== ADDRESS: synthetic ================================================================================================

    [Theory]
    [InlineData("Address: 123 Main AVE", 12)]
    [InlineData("Address: 5 Oak BLVD", 10)]
    [InlineData("Address: 12345A Pine RD", 14)]
    [InlineData("Address: 1 A B C D ST", 12)]                  // four street-name tokens
    [InlineData("Address: 123 main st", 11)]                    // [EA] the suffix matches ASCII-case-insensitively
    [InlineData("Address: 123 O'Brien-Smith Jr. ST", 24)]
    [InlineData("Address: PO Box 7B-2", 11)]
    public void Address_PathA_AcceptsTheClosedStreetAndBoxForms(string raw, int length) // synthetic
    {
        Assert.Equal(length, Assert.Single(Address(raw)).Span.Length);
    }

    [Theory]
    [InlineData("Address: 0 Main ST")]                          // street number must start 1-9
    [InlineData("Address: 1234567 Main ST")]                    // seven digits
    [InlineData("Address: 123AB Main ST")]
    [InlineData("Address: 123  Main ST")]                       // two spaces
    [InlineData("Address: 123 Main  ST")]
    [InlineData("Address: 123 Main STREET")]                    // not in the snapshot
    [InlineData("Address: 123 Main")]
    [InlineData("Address: 123 ST")]                              // no street-name token
    [InlineData("Address: Main ST")]
    [InlineData("Address: 123 One Two Three Four Five ST")]
    [InlineData("Address: PO Box")]
    [InlineData("Address: po box 12")]                          // exact "PO Box"
    [InlineData("Address: RR 0 Box 15")]
    [InlineData("Address: RR 12345 Box 15")]
    [InlineData("Address: HC 1 box 22")]
    [InlineData("Addresses: 123 Main ST")]
    [InlineData("Addr: 123 Main ST")]
    [InlineData("Location: 123 Main ST")]
    [InlineData("Address:    123 Main ST")]                     // four spaces after the separator
    public void Address_PathA_NearMisses_AreNoCandidate(string raw) => Assert.Empty(Address(raw)); // synthetic

    [Theory]
    [InlineData("123 Main ST, New York, NY 10001", 31)]
    [InlineData("123 Main ST New York NY 10001", 29)]           // a single space is a separator, too
    [InlineData("123 Main ST,New York,NY 10001", 29)]
    [InlineData("123 Main ST, New York,NY 10001", 30)]
    [InlineData("123 Main ST, Albany, CA 90210", 29)]
    [InlineData("123 Main ST APT 4B, New York, NY 10001", 38)]
    [InlineData("Home: 123 Main ST, Santa Cruz, CA 95060", 33)]
    [InlineData("1 A B C D ST, A B C D, PR 00901", 31)]
    public void Address_PathB_AcceptsTheClosedForms(string raw, int length) // synthetic
    {
        var candidate = Assert.Single(Address(raw));

        Assert.Equal(length, candidate.Span.Length);
        Assert.Equal(ResolutionEvidence.SJP, candidate.Evidence);
    }

    [Theory]
    [InlineData("123 Main ST, New York, NY")]                    // no ZIP
    [InlineData("123 Main ST, New York 10001")]                  // no state
    [InlineData("123 Main ST, NY 10001")]                        // no city
    [InlineData("Main ST, New York, NY 10001")]                  // no street number
    [InlineData("123 Main, New York, NY 10001")]                 // no suffix
    [InlineData("123 Main ST, New York, ny 10001")]              // [EA] upper-case state
    [InlineData("123 Main ST, New York, NY  10001")]             // two spaces before the ZIP
    [InlineData("123 Main ST, New York, NY, 10001")]
    [InlineData("123 Main ST, New York, NY 100011")]
    [InlineData("123 Main ST, New York, NY 10001-12")]
    [InlineData("123 Main ST, New York, NY 10001a")]
    [InlineData("123 Main ST, One Two Three Four Five, NY 10001")] // five city tokens
    [InlineData("123 Main ST, N3w York, NY 10001")]              // a digit in the city
    [InlineData("a123 Main ST, New York, NY 10001")]
    public void Address_PathB_NearMisses(string raw) => Assert.Empty(Address(raw)); // synthetic

    [Fact]
    public void Address_ZipAloneOrAStateAlone_NeverMakeACandidate_InAnyLine() // synthetic
    {
        Assert.Empty(Address("10001"));
        Assert.Empty(Address("NY 10001"));
        Assert.Empty(Address("New York, NY 10001"));
        Assert.Empty(Address("Address: New York, NY 10001"));
    }

    [Fact]
    public void Address_ALabelledAddress_WithACityStateAndZip_CoversTheWholeBoundField() // literal D2.2: Path A ends at the field boundary
    {
        const string raw = "Address: 123 Main ST, New York, NY 10001; memo";

        var candidate = Assert.Single(Address(raw));

        Assert.Equal(new RawSpan(9, 31), candidate.Span);
        Assert.Equal(ResolutionEvidence.SC, candidate.Evidence); // the labelled reading wins over the unlabelled one for the same span
    }

    [Fact]
    public void Address_TheNinetySixLimit_DoesNotApplyToAnUnlabelledPathBLine() // Path B is bounded by the 160-character cap instead
    {
        string pathB = "123 " + new string('A', 40) + " " + new string('B', 40) + " ST, New York, NY 10001";

        Assert.Single(Address(pathB));
        Assert.Empty(Address("1 " + string.Join(" ", Enumerable.Repeat(new string('A', 40), 5)) + " ST, New York, NY 10001"));
    }

    [Fact]
    public void Address_SpansAfterEmoji_ArePreserved_AndLevelAndConfidenceAreLevel2High()
    {
        const string prefix = "\U0001F600\U0001F4A9 ";
        string raw = prefix + "Address: 123 Main ST\n" + prefix + "123 Main ST, New York, NY 10001";

        var found = Address(raw);

        Assert.Equal(2, found.Count);
        Assert.Equal(new RawSpan(prefix.Length + 9, 11), found[0].Span);
        Assert.All(found, c =>
        {
            Assert.Equal(RiskLevel.Level2, c.RiskLevel);
            Assert.Equal(DetectionConfidence.High, c.Confidence);
        });
    }

    [Fact]
    public void Address_ABoundedWork_OnManyLinesAndALongRun()
    {
        string many = string.Join("\n", Enumerable.Repeat("123 Main ST, New York, NY 10001", 4_000));
        string run = string.Concat(Enumerable.Repeat("123 Main ", 60_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(4_000, Address(many).Count);
        Assert.Empty(Address(run));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    // ===== NAME: frozen D3A rows =============================================================================================

    [Theory]
    [InlineData("Full Name: Alice O'Neil", "us.name|Name|L2|C|11:12|Alice O'Neil|[이름1]|350")]                  // P01
    [InlineData("Name: Alice O'Neil", "us.name|Name|L1|C|6:12|Alice O'Neil|[이름1]|350")]                        // P02
    [InlineData("Dr. Alice O'Neil", "us.name|Name|L1|C|0:16|Alice O'Neil|[이름1]|350")]                          // P03
    [InlineData("Name: Alice O'Neil, CPA", "us.name|Name|L1|C|6:12|Alice O'Neil|[이름1]|350")]                   // COMMA
    [InlineData("Full Name: Alice Beth Carol Doe", "us.name|Name|L2|C|11:20|Alice Beth Carol Doe|[이름1]|350")]  // FOUR
    [InlineData("Full Name: J. Doe", "us.name|Name|L2|C|11:6|J. Doe|[이름1]|350")]                               // INITIAL
    [InlineData("Family Name: O'Neil", "us.name|Name|L2|C|13:6|O'Neil|[이름1]|350")]                             // LBL-FAMILY
    [InlineData("First Name: Alice", "us.name|Name|L2|C|12:5|Alice|[이름1]|350")]                                // LBL-FIRST
    [InlineData("Given Name: Alice", "us.name|Name|L2|C|12:5|Alice|[이름1]|350")]                                // LBL-GIVEN
    [InlineData("Last Name: O'Neil", "us.name|Name|L2|C|11:6|O'Neil|[이름1]|350")]                               // LBL-LAST
    [InlineData("Surname: O'Neil", "us.name|Name|L2|C|9:6|O'Neil|[이름1]|350")]                                  // LBL-SURNAME
    [InlineData("Mr Alice Doe", "us.name|Name|L1|C|0:12|Alice Doe|[이름1]|350")]                                 // HON-01
    [InlineData("Mr. Alice Doe", "us.name|Name|L1|C|0:13|Alice Doe|[이름1]|350")]                                // HON-02
    [InlineData("Mrs Alice Doe", "us.name|Name|L1|C|0:13|Alice Doe|[이름1]|350")]                                // HON-03
    [InlineData("Mrs. Alice Doe", "us.name|Name|L1|C|0:14|Alice Doe|[이름1]|350")]                               // HON-04
    [InlineData("Ms Alice Doe", "us.name|Name|L1|C|0:12|Alice Doe|[이름1]|350")]                                 // HON-05
    [InlineData("Ms. Alice Doe", "us.name|Name|L1|C|0:13|Alice Doe|[이름1]|350")]                                // HON-06
    [InlineData("Miss Alice Doe", "us.name|Name|L1|C|0:14|Alice Doe|[이름1]|350")]                               // HON-07
    [InlineData("Dr Alice Doe", "us.name|Name|L1|C|0:12|Alice Doe|[이름1]|350")]                                 // HON-08
    public void Name_FrozenPositives_MatchTheOracleRecordExactly(string raw, string expected) =>
        Assert.Equal(expected, Oracle(Assert.Single(Name(raw))));

    [Theory]
    [InlineData("Full Name: Alice Beth Carol Dana Doe")]   // FIVE
    [InlineData("Alice O'Neil")]                           // FN01
    [InlineData("Prof. Alice O'Neil")]                     // N01
    [InlineData("Dr. Alice")]                              // NEG-HON1
    [InlineData("Full Name: Phone: Alice Doe")]            // NEG-NEXTLABEL
    [InlineData("Full Name: Alice/Doe")]                   // NEG-SLASH
    [InlineData("Full Name: Alíce Doe")]              // NEG-UNICODE
    [InlineData("Manager Alice Doe")]                      // ROLE
    public void Name_FrozenNegatives_ProduceNoCandidate(string raw) => Assert.Empty(Name(raw));

    // ===== NAME: levels, confidence, grammar =================================================================================

    [Fact]
    public void Name_SpecificLabels_AreLevel2High_AndTheGenericLabelAndHonorificsAreLevel1Medium()
    {
        foreach (string raw in new[] { "Full Name: Alice Doe", "First Name: Alice", "Given Name: Alice", "Last Name: Doe", "Family Name: Doe", "Surname: Doe" })
        {
            var c = Assert.Single(Name(raw));
            Assert.Equal(RiskLevel.Level2, c.RiskLevel);
            Assert.Equal(DetectionConfidence.High, c.Confidence);
        }

        foreach (string raw in new[] { "Name: Alice Doe", "Mr Alice Doe", "Dr. Alice Doe" })
        {
            var c = Assert.Single(Name(raw));
            Assert.Equal(RiskLevel.Level1, c.RiskLevel);
            Assert.Equal(DetectionConfidence.Medium, c.Confidence);
        }
    }

    [Theory]
    [InlineData("Full Name: Alice", 11, 5)]
    [InlineData("Full Name: Mary-Jane O'Hara-Smith", 11, 22)]
    [InlineData("Full Name: A B C D", 11, 7)]
    [InlineData("Full Name: Alice Doe.", 11, 9)]               // terminal period is not part of the name
    [InlineData("Full Name: Alice Doe?", 11, 9)]
    [InlineData("Full Name: Alice Doe, Jr.", 11, 9)]
    [InlineData("Full Name: Alice Doe; memo", 11, 9)]
    [InlineData("Full Name: Alice Doe\tmemo", 11, 9)]
    [InlineData("full name: alice doe", 11, 9)]                  // [EA] ASCII-case-insensitive label; tokens as typed
    [InlineData("Full Name: Alice J.", 11, 8)]                   // an initial keeps its period
    public void Name_TheTokenGrammar_AndTheStopCharacters(string raw, int start, int length) // synthetic
    {
        Assert.Equal(new RawSpan(start, length), Assert.Single(Name(raw)).Span);
    }

    [Theory]
    [InlineData("Full Name: Alice 5")]                           // a non-token after a space
    [InlineData("Full Name: 5lice Doe")]
    [InlineData("Full Name: Alice_Doe")]
    [InlineData("Full Name: Alice@Doe")]
    [InlineData("Full Name: Alice (Doe)")]
    [InlineData("Full Name: 'Alice Doe")]
    [InlineData("Full Name: -Alice Doe")]
    [InlineData("Full Name:    Alice Doe")]                     // four spaces after the separator
    [InlineData("Full Name: ")]
    [InlineData("Full Names: Alice Doe")]                        // the label sits inside a longer word
    [InlineData("Username: Alice Doe")]
    [InlineData("Nickname: Alice Doe")]
    [InlineData("Full Name: Alice  Doe")]                        // two spaces between tokens
    [InlineData("Full Name: Alｉce Doe")]
    [InlineData("Full Name: AliceAliceAliceAliceAliceAliceAliceAliceAliceAlice Doe")] // a token of 50 letters (> 40)
    [InlineData("Mr")]
    [InlineData("Mr Alice")]
    [InlineData("Mr Alice Beth Carol Dana Doe")]                 // five tokens after the honorific
    [InlineData("mr Alice Doe")]                                 // [EA] honorifics are matched case-sensitively
    [InlineData("MR Alice Doe")]
    [InlineData("Mx Alice Doe")]
    [InlineData("Mx. Alice Doe")]
    [InlineData("Full Name: Élise Doe")]               // a non-ASCII FIRST letter
    [InlineData("Surname: Ａlice")]
    [InlineData("Mrs.Alice Doe")]
    [InlineData("Mr  Alice Doe")]
    public void Name_NearMisses_AreNoCandidate(string raw) => Assert.Empty(Name(raw)); // synthetic

    [Fact]
    public void Name_TheHonorific_NeedsATokenBoundaryBefore_AndStopsBeforeFollowingPunctuation() // synthetic
    {
        Assert.Empty(Name("SMr Alice Doe"));
        Assert.Empty(Name("1Dr Alice Doe"));
        Assert.Equal(new RawSpan(4, 13), Assert.Single(Name("Hi, Dr. Alice Doe")).Span);
        var c = Assert.Single(Name("Hi; Mr Alice Doe, welcome"));
        Assert.Equal("Alice Doe", c.Canonical!.Value.Value);
        Assert.Equal(new RawSpan(4, 12), c.Span);
    }

    [Fact]
    public void Name_AStandaloneCapitalisedWord_AJobTitleOrProse_NeverMakeACandidate() // synthetic, D2.2: no inference
    {
        foreach (string raw in new[] { "Alice", "ALICE DOE", "Alice Doe wrote this", "Director Alice Doe", "CEO Alice Doe", "the quick Alice Doe", "Dear Alice" })
        {
            Assert.Empty(Name(raw));
        }
    }

    [Fact]
    public void Name_TheCanonicalValue_IsTheTokensAsTyped_WithoutTheHonorific()
    {
        Assert.Equal("Alice Doe", Assert.Single(Name("Ms. Alice Doe")).Canonical!.Value.Value);
        Assert.Equal("Alice Doe", Assert.Single(Name("Name: Alice Doe")).Canonical!.Value.Value);
        Assert.NotEqual(Assert.Single(Name("Name: Alice Doe")).Canonical, Assert.Single(Name("Name: alice doe")).Canonical);
    }

    [Fact]
    public void Name_SpansAfterEmoji_RepeatsAndTwoFields_AreExactRawSpans()
    {
        const string prefix = "\U0001F600\U0001F4A9 ";
        string raw = prefix + "Full Name: Alice O'Neil; Surname: O'Neil\nName: Alice O'Neil";

        var found = Name(raw);

        Assert.Equal(3, found.Count);
        Assert.Equal(new RawSpan(prefix.Length + 11, 12), found[0].Span);
        Assert.Equal(found[0].Canonical, found[2].Canonical);
    }

    [Fact]
    public void Name_ALabelFollowedByAnotherLabel_IsAnEmptyField()
    {
        Assert.Empty(Name("Full Name: Email: a@b.com"));
        Assert.Empty(Name("Name: Phone: 212-555-1212"));
    }

    [Fact]
    public void Name_ABoundedWork_OnManyFieldsAndALongRun()
    {
        string many = string.Join("\n", Enumerable.Repeat("Full Name: Alice O'Neil", 4_000));
        string run = "Full Name: " + string.Join(" ", Enumerable.Repeat("Alice", 100_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(4_000, Name(many).Count);
        Assert.Empty(Name(run));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    // ===== no raw value in any diagnostic string =============================================================================

    [Fact]
    public void NoDiagnosticString_ShowsTheNewValues()
    {
        string[] secrets = ["2125551212", "212-555-1212", "123 Main ST", "Alice", "O'Neil"];
        const string raw = "Phone: 212-555-1212; Address: 123 Main ST; Full Name: Alice O'Neil";
        var found = Phone(raw).Concat(Address(raw)).Concat(Name(raw)).ToList();
        Assert.Equal(3, found.Count);

        foreach (var c in found)
        {
            foreach (string secret in secrets)
            {
                Assert.DoesNotContain(secret, c.ToString());
                Assert.DoesNotContain(secret, c.Canonical!.Value.ToString());
                Assert.DoesNotContain(secret, c.DetectorName);
            }
        }

        foreach (IDetector d in new IDetector[] { new UsPhoneCandidateDetector(Authority), new UsAddressCandidateDetector(Authority), new UsNameCandidateDetector() })
        {
            foreach (string secret in secrets)
            {
                Assert.DoesNotContain(secret, d.ToString());
            }
        }
    }
}
