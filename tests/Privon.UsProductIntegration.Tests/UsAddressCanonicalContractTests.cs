using Privon.Core;
using Privon.Detection;
using Privon.Detection.Scope;
using Privon.UsProductIntegration;
using static Privon.UsProductIntegration.Tests.UsProductHarness;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY. What the address CanonicalValue is, and why. Two different questions are kept apart:
//   A. does the frozen D3A `null` in an address row mean "no canonical value is DEFINED by the contract"?
//   B. must the RUNTIME candidate's CanonicalValue be null?
// The evidence below answers A with yes and B with no: B cannot hold together with the frozen render, because the product's alias
// stage keys every alias on the canonical value and has no other approved route. Nothing in this file rewrites a canonical value to
// make a comparison pass: the canonical is asserted as what it is -- PRESENT, the matched text.
public class UsAddressCanonicalContractTests
{
    private static readonly UsAuthoritySet Authority = UsTestAuthorityProvider.ValidSet();

    private static IReadOnlyList<DetectionCandidate> Pipe(string raw) => UsOptInPipeline.Create(authority: Authority).Detect(raw).Candidates;

    private static DetectionCandidate AbsentCanonicalAddress(RawSpan span) =>
        new(PiiType.Address, span, RiskLevel.Level2, DetectionConfidence.High, Canonical: null, DetectorName: "Probe",
            DetectorId: DetectorId.Create("us.address"), Evidence: ResolutionEvidence.SC, ResolutionRank: 330);

    // ===== B: a runtime-null address cannot be protected, however the alias is routed ======================================

    [Fact]
    public void AnAbsentCanonical_ProtectCandidate_GetsNoAlias_AndTheReplacerFailsClosed_WithoutTheText()
    {
        const string raw = "Address: 123 Main ST";
        var decisions = CandidatePolicyEvaluator.Evaluate([new EvaluatedCandidate(AbsentCanonicalAddress(new RawSpan(9, 11)), TrustState.Unknown)]);

        Assert.Equal(CandidateDisposition.Protect, Assert.Single(decisions).Disposition); // Level 2, untrusted: it IS meant to be protected
        var assignments = AliasAssigner.Assign(decisions, new AliasMap());

        Assert.Null(Assert.Single(assignments).Alias); // D3B0-A A.9 7.2: ABSENT registers no alias, nothing is invented
        var ex = Assert.Throws<ArgumentException>(() => AliasReplacer.Apply(raw, assignments));
        Assert.DoesNotContain("123 Main", ex.Message);
    }

    [Fact]
    public void ThereIsNoApprovedInternalRoute_ACandidateStampedWithAnAliasIdentity_IsStillNotAliased()
    {
        // DetectionCandidate carries an optional neutral AliasIdentity, but the alias stage never reads it: AliasIdentity is derived
        // from (type, canonical) downstream. So an absent canonical cannot be rescued by pre-stamping the identity.
        var stamped = AbsentCanonicalAddress(new RawSpan(9, 11)) with
        {
            AliasIdentity = new AliasIdentity(AliasFamilyTable.FamilyOf(PiiType.Address), 1),
        };
        var decisions = CandidatePolicyEvaluator.Evaluate([new EvaluatedCandidate(stamped, TrustState.Unknown)]);

        Assert.Null(Assert.Single(AliasAssigner.Assign(decisions, new AliasMap())).Alias);
    }

    [Fact]
    public void AnAbsentCanonical_IsAcceptedByTheResolver_SoTheProblemIsNotInDetection_ButInTheAliasStage()
    {
        var resolved = CandidateResolver.Resolve(new ResolutionInput([AbsentCanonicalAddress(new RawSpan(9, 11))], 20));

        Assert.Single(resolved.Winners);
        Assert.False(Assert.Single(resolved.Winners).Canonical.HasValue);
    }

    // ===== the stamped canonical: present, exact, stable, distinct ==========================================================

    [Theory]
    [InlineData("Address: 123 Main ST", "123 Main ST")]
    [InlineData("Home Address: 123 Main ST APT 4B", "123 Main ST APT 4B")]
    [InlineData("Mailing Address: PO Box 123", "PO Box 123")]
    [InlineData("Address: RR 2 Box 15", "RR 2 Box 15")]
    [InlineData("123 Main ST, New York, NY 10001", "123 Main ST, New York, NY 10001")]
    [InlineData("see 123 Main ST, New York, NY 10001-1234.", "123 Main ST, New York, NY 10001-1234")]
    public void EveryAddressCandidate_CarriesAPresentCanonical_EqualToItsExactMatchedText(string raw, string expected)
    {
        var candidate = Assert.Single(Pipe(raw));

        Assert.True(candidate.Canonical.HasValue);
        Assert.Equal(PiiType.Address, candidate.Canonical!.Value.PiiType);
        Assert.Equal(expected, candidate.Canonical.Value.Value);
        Assert.Equal(expected, raw.Substring(candidate.Span.Start, candidate.Span.Length)); // span and canonical say the same thing
    }

    [Fact]
    public void TheSameAddress_HasTheSameCanonical_WhereverAndHoweverItIsFound_AndAcrossRepeatedRuns()
    {
        var a = Assert.Single(Pipe("Address: 123 Main ST")).Canonical;
        var b = Assert.Single(Pipe("Memo; Home Address: 123 Main ST; memo")).Canonical;
        var c = Assert.Single(Pipe("\U0001F600\U0001F4A9 Address: 123 Main ST")).Canonical;
        var again = Assert.Single(Pipe("Address: 123 Main ST")).Canonical;

        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.Equal(a, again);
    }

    [Fact]
    public void DifferentAddresses_HaveDifferentCanonicals_AndTheStampedTextIsNotNormalised() // [EA] exact-text identity
    {
        string[] raws =
        [
            "Address: 123 Main ST", "Address: 124 Main ST", "Address: 123 Main AVE", "Address: 123 Main ST APT 4B",
            "Address: 123 main st", "123 Main ST, New York, NY 10001", "123 Main ST, New York, NY 10002",
        ];

        var canonicals = raws.Select(r => Assert.Single(Pipe(r)).Canonical!.Value.Value).ToList();

        Assert.Equal(raws.Length, canonicals.Distinct(StringComparer.Ordinal).Count());
    }

    // ===== the alias chain: candidate -> policy -> alias -> rewrite ========================================================

    private static string Rewrite(string raw)
    {
        var decisions = CandidatePolicyEvaluator.Evaluate(ExceptionTrustedEvaluator.Evaluate(UsOptInPipeline.Create(authority: Authority).Detect(raw), [], []));
        return AliasReplacer.Apply(raw, AliasAssigner.Assign(decisions, new AliasMap()));
    }

    [Fact]
    public void ARepeatedAddress_GetsOneStableAlias_AndADifferentAddressTheNext_InTheRealRewrite()
    {
        const string raw =
            "Address: 123 Main ST\nAddress: 456 Oak AVE\nAddress: 123 Main ST\n123 Main ST, New York, NY 10001\nAddress: 456 Oak AVE";

        Assert.Equal("Address: [주소1]\nAddress: [주소2]\nAddress: [주소1]\n[주소3]\nAddress: [주소2]", Rewrite(raw));
    }

    [Theory]
    [InlineData("Address: 123 Main ST", "Address: [주소1]")]
    [InlineData("Home Address: 123 Main ST; memo", "Home Address: [주소1]; memo")]
    [InlineData("Address: 123 Main ST APT 4B", "Address: [주소1]")]
    [InlineData("Mailing Address: PO Box 123", "Mailing Address: [주소1]")]
    [InlineData("123 Main ST, New York, NY 10001.", "[주소1].")]
    [InlineData("\U0001F600 Address: 123 Main ST", "\U0001F600 Address: [주소1]")]
    public void TheRealProtectRender_IsTheFrozenOne_WithTheExactSpanReplaced(string raw, string expected) =>
        Assert.Equal(expected, Rewrite(raw));

    [Fact]
    public void TheRewrite_LeavesNoPartOfTheAddressBehind()
    {
        string rewritten = Rewrite("Address: 123 Main ST APT 4B; Phone: x");

        Assert.DoesNotContain("123", rewritten);
        Assert.DoesNotContain("Main", rewritten);
        Assert.DoesNotContain("APT", rewritten);
    }

    [Fact]
    public void TheOtherTwelveTypes_KeepTheirExactAliasTokens_WhetherOrNotAnAddressIsPresent()
    {
        const string twelve =
            "Passport Number: P12345678 State: NY\nA-Number: A1234567\nI-94 Number: 123456789AB\n" +
            "ATIN: 912-93-4567\nITIN: 912-70-1234\nSSN: 321-54-9876\nMBI: 1A23C45DE67\nSEVIS: N1234567890\n" +
            "Driver License Number: D123-4567 State: CA\nRouting Number: 120000003 | Account Number: AB12CD34\n" +
            "Phone: 212-555-1212\nFull Name: Alice O'Neil";
        string withAddress = "Address: 123 Main ST\n" + twelve + "\n123 Main ST, New York, NY 10001";

        static List<string> Tokens(string raw, UsAuthoritySet authority)
        {
            var map = new AliasMap();
            return UsOptInPipeline.Create(authority: authority).Detect(raw).Candidates
                .Where(c => c.PiiType != PiiType.Address)
                .OrderBy(c => c.Span.Start)
                .Select(c => $"{c.DetectorId.Value}|{map.GetOrAdd(c.Canonical!.Value).Value}")
                .ToList();
        }

        Assert.Equal(12, Tokens(twelve, Authority).Count);
        Assert.Equal(Tokens(twelve, Authority), Tokens(withAddress, Authority));
    }

    // ===== privacy ========================================================================================================

    [Fact]
    public void NoDiagnosticString_ShowsTheAddress_NotTheCandidate_NotTheCanonical_NotTheMatchRecord_NotAnException()
    {
        const string address = "123 Main ST";
        const string raw = "Address: 123 Main ST";
        var candidate = Assert.Single(Pipe(raw));
        var diagnostics = new List<string>
        {
            candidate.ToString(), candidate.Canonical!.Value.ToString(), candidate.DetectorName, candidate.DetectorId.ToString(),
            new UsAddressCandidateDetector(Authority).ToString() ?? string.Empty,
            UsOptInPipeline.Create(authority: Authority).Detect(raw).ToString() ?? string.Empty,
        };

        var assembly = typeof(UsAddressCandidateDetector).Assembly;
        var matchType = assembly.GetType("Privon.UsProductIntegration.UsDocumentMatch", throwOnError: true)!;
        var evidenceType = assembly.GetType("Privon.UsProductIntegration.UsDocumentEvidence", throwOnError: true)!;
        diagnostics.Add(Activator.CreateInstance(
            matchType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [new RawSpan(9, 11), Enum.ToObject(evidenceType, 3), address, OwnerClaim.NotApplicable, RiskLevel.Level2, DetectionConfidence.High],
            culture: null)!.ToString()!);

        var missing = Assert.Throws<RequiredDetectorNotRegisteredException>(() =>
            UsOptInPipeline.Create(
                unitedStatesSetting: CountrySettingObservation.Missing,
                usDetectorFactory: () => UsIntegrationCandidates.CreateDetectors(Authority).Where(d => d.Id.Value != "us.name").ToList(),
                authority: Authority).Detect(raw));
        diagnostics.Add(missing.Message);
        diagnostics.Add(missing.ToString());

        foreach (string text in diagnostics)
        {
            Assert.DoesNotContain(address, text);
            Assert.DoesNotContain("Main", text);
        }
    }
}
