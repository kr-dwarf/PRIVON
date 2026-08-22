using Privon.Core;
using Privon.Detection;

namespace Privon.Detection.Tests;

// Phase 2T.1 — Alias Foundation tests (AliasToken/AliasLabelProvider/AliasMap/AliasAssigner).
// All fixtures are synthetic filler, never real PII.
public class AliasFoundationTests
{
    private static CandidatePolicyDecision Decision(PiiType type, string canonicalValue, int start, CandidateDisposition disposition, int length = 5) =>
        new(new EvaluatedCandidate(
            new DetectionCandidate(type, new RawSpan(start, length), RiskLevel.Level2, DetectionConfidence.High, new CanonicalValue(type, canonicalValue), "Test"),
            TrustState.Untrusted), disposition);

    // ---- 1. all 9 PiiType labels exactly as decided ----
    [Theory]
    [InlineData(PiiType.Phone, "전화번호")]
    [InlineData(PiiType.Email, "이메일")]
    [InlineData(PiiType.ResidentRegistrationNumber, "주민등록번호")]
    [InlineData(PiiType.CardNumber, "카드번호")]
    [InlineData(PiiType.BankAccountNumber, "계좌번호")]
    [InlineData(PiiType.Secret, "비밀정보")]
    [InlineData(PiiType.IpAddress, "IP주소")]
    [InlineData(PiiType.MacAddress, "MAC주소")]
    [InlineData(PiiType.GpsCoordinate, "GPS좌표")]
    public void AliasLabel_MatchesConfirmedMapping(PiiType type, string expectedLabel)
    {
        Assert.Equal(expectedLabel, AliasLabelProvider.GetLabel(type));
    }

    // ---- 2. completeness over all current PiiType members ----
    [Fact]
    public void AllCurrentPiiTypes_HaveALabel()
    {
        foreach (var type in Enum.GetValues<PiiType>())
        {
            var label = AliasLabelProvider.GetLabel(type);
            Assert.False(string.IsNullOrWhiteSpace(label));
        }
    }

    // ---- 3. undefined PiiType -> fail-fast ----
    [Fact]
    public void UndefinedPiiType_ThrowsFast()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AliasLabelProvider.GetLabel((PiiType)999));
    }

    // ---- 4/5/6. numbering: first value, second different value, independent per-type counter ----
    [Fact]
    public void FirstPhone_GetsNumberOne()
    {
        var map = new AliasMap();
        var token = map.GetOrAdd(new CanonicalValue(PiiType.Phone, "01011112222"));
        Assert.Equal("[전화번호1]", token.Value);
        Assert.Equal(1, token.Number);
    }

    [Fact]
    public void SecondDifferentPhone_GetsNumberTwo()
    {
        var map = new AliasMap();
        map.GetOrAdd(new CanonicalValue(PiiType.Phone, "01011112222"));
        var second = map.GetOrAdd(new CanonicalValue(PiiType.Phone, "01033334444"));
        Assert.Equal("[전화번호2]", second.Value);
    }

    [Fact]
    public void EmailCounter_IndependentFromPhoneCounter()
    {
        var map = new AliasMap();
        map.GetOrAdd(new CanonicalValue(PiiType.Phone, "01011112222"));
        var email = map.GetOrAdd(new CanonicalValue(PiiType.Email, "user@example.com"));
        Assert.Equal("[이메일1]", email.Value);
    }

    // ---- 7. same canonical repeated -> same token ----
    [Fact]
    public void SameCanonicalValue_ReusesSameToken()
    {
        var map = new AliasMap();
        var first = map.GetOrAdd(new CanonicalValue(PiiType.Phone, "01011112222"));
        var second = map.GetOrAdd(new CanonicalValue(PiiType.Phone, "01011112222"));
        Assert.Equal(first, second);
    }

    // ---- 8. same raw-looking value, different PiiType -> different typed alias ----
    [Fact]
    public void SameStringDifferentPiiType_DifferentAlias()
    {
        var map = new AliasMap();
        var phone = map.GetOrAdd(new CanonicalValue(PiiType.Phone, "01011112222"));
        var account = map.GetOrAdd(new CanonicalValue(PiiType.BankAccountNumber, "01011112222"));

        Assert.NotEqual(phone.Value, account.Value);
        Assert.Equal("[전화번호1]", phone.Value);
        Assert.Equal("[계좌번호1]", account.Value);
    }

    // ---- 9/10/11. only Protect gets an alias ----
    [Fact]
    public void Protect_HasAlias_Bypass_And_NeedsDecision_DoNot()
    {
        var protectDecision = Decision(PiiType.Phone, "01011112222", start: 0, CandidateDisposition.Protect);
        var bypassDecision = Decision(PiiType.Phone, "01099998888", start: 20, CandidateDisposition.Bypass);
        var needsDecisionDecision = Decision(PiiType.GpsCoordinate, "1.0,2.0", start: 40, CandidateDisposition.NeedsDecision);

        var results = AliasAssigner.Assign([protectDecision, bypassDecision, needsDecisionDecision], new AliasMap());

        Assert.NotNull(results[0].Alias);
        Assert.Null(results[1].Alias);
        Assert.Null(results[2].Alias);
    }

    // ---- 12/13. numbering follows raw appearance order, independent of input list order ----
    [Fact]
    public void NumberingFollowsRawSpanOrder_NotInputListOrder()
    {
        // Phone B appears earlier in raw text (Start=0) but is LATER in the input list;
        // Phone A appears later in raw text (Start=50) but is FIRST in the input list --
        // simulating OverlapResolver's risk/confidence-driven order, not raw-span order.
        var phoneALaterInText = Decision(PiiType.Phone, "01011112222", start: 50, CandidateDisposition.Protect);
        var phoneBEarlierInText = Decision(PiiType.Phone, "01099998888", start: 0, CandidateDisposition.Protect);

        var results = AliasAssigner.Assign([phoneALaterInText, phoneBEarlierInText], new AliasMap());

        // Output order still matches input order...
        Assert.Equal(phoneALaterInText, results[0].Decision);
        Assert.Equal(phoneBEarlierInText, results[1].Decision);
        // ...but numbering reflects raw appearance: B (Start=0) got [전화번호1], A (Start=50) got [전화번호2].
        Assert.Equal("[전화번호2]", results[0].Alias!.Value.Value);
        Assert.Equal("[전화번호1]", results[1].Alias!.Value.Value);
    }

    // ---- 14/21. duplicate spans of the same canonical value consume exactly one number ----
    [Fact]
    public void DuplicateCanonicalAcrossSpans_ConsumesOnlyOneNumber()
    {
        var span1 = Decision(PiiType.Phone, "01011112222", start: 0, CandidateDisposition.Protect);
        var span2 = Decision(PiiType.Phone, "01011112222", start: 30, CandidateDisposition.Protect);

        var results = AliasAssigner.Assign([span1, span2], new AliasMap());

        Assert.Equal("[전화번호1]", results[0].Alias!.Value.Value);
        Assert.Equal("[전화번호1]", results[1].Alias!.Value.Value);
    }

    // ---- 15. counters independent per PiiType (broader multi-type check) ----
    [Fact]
    public void CountersAreIndependentPerPiiType()
    {
        var map = new AliasMap();
        var phone1 = map.GetOrAdd(new CanonicalValue(PiiType.Phone, "01011112222"));
        var ip1 = map.GetOrAdd(new CanonicalValue(PiiType.IpAddress, "203.0.113.1"));
        var phone2 = map.GetOrAdd(new CanonicalValue(PiiType.Phone, "01033334444"));

        Assert.Equal("[전화번호1]", phone1.Value);
        Assert.Equal("[IP주소1]", ip1.Value);
        Assert.Equal("[전화번호2]", phone2.Value);
    }

    // ---- 16. no number reuse -- a later-registered NEW canonical keeps advancing the counter ----
    [Fact]
    public void NoNumberReuse_NewCanonicalAlwaysAdvancesCounter()
    {
        var map = new AliasMap();
        map.GetOrAdd(new CanonicalValue(PiiType.Phone, "01011112222")); // 1
        map.GetOrAdd(new CanonicalValue(PiiType.Phone, "01033334444")); // 2
        // "Phone A deleted from the text" has no meaning to AliasMap itself -- it never
        // revisits/frees a number. A brand new canonical value always gets the next number.
        var third = map.GetOrAdd(new CanonicalValue(PiiType.Phone, "01055556666"));

        Assert.Equal("[전화번호3]", third.Value);
    }

    // ---- 17. same AliasMap instance reused across multiple Assign calls ("revisions") stays stable ----
    [Fact]
    public void SameAliasMapAcrossMultipleAssignCalls_StaysStable()
    {
        var map = new AliasMap();

        var revision1 = Decision(PiiType.Phone, "01011112222", start: 0, CandidateDisposition.Protect);
        var revision1Results = AliasAssigner.Assign([revision1], map);
        Assert.Equal("[전화번호1]", revision1Results[0].Alias!.Value.Value);

        // Revision 2: same Phone A plus a new Phone B.
        var phoneAAgain = Decision(PiiType.Phone, "01011112222", start: 0, CandidateDisposition.Protect);
        var phoneBNew = Decision(PiiType.Phone, "01099998888", start: 20, CandidateDisposition.Protect);
        var revision2Results = AliasAssigner.Assign([phoneAAgain, phoneBNew], map);

        Assert.Equal("[전화번호1]", revision2Results[0].Alias!.Value.Value); // stable
        Assert.Equal("[전화번호2]", revision2Results[1].Alias!.Value.Value); // new

        // Revision 3: Phone A removed from text, Phone C (brand new) added -- no reuse of "1".
        var phoneCNew = Decision(PiiType.Phone, "01077778888", start: 0, CandidateDisposition.Protect);
        var revision3Results = AliasAssigner.Assign([phoneCNew], map);
        Assert.Equal("[전화번호3]", revision3Results[0].Alias!.Value.Value);
    }

    // ---- 18. a fresh AliasMap instance always starts numbering over from 1 ----
    [Fact]
    public void NewAliasMapInstance_NumberingRestartsFromOne()
    {
        var map1 = new AliasMap();
        map1.GetOrAdd(new CanonicalValue(PiiType.Phone, "01011112222"));
        map1.GetOrAdd(new CanonicalValue(PiiType.Phone, "01033334444"));

        var map2 = new AliasMap();
        var token = map2.GetOrAdd(new CanonicalValue(PiiType.Phone, "01099998888"));

        Assert.Equal("[전화번호1]", token.Value);
    }

    // ---- 19/20. Bypass/NeedsDecision appearing earlier in raw text never consumes a number ----
    [Fact]
    public void BypassEarlierInText_DoesNotConsumeNumber()
    {
        var bypassEarlier = Decision(PiiType.Phone, "01000000000", start: 0, CandidateDisposition.Bypass);
        var protectLater = Decision(PiiType.Phone, "01011112222", start: 50, CandidateDisposition.Protect);

        var results = AliasAssigner.Assign([bypassEarlier, protectLater], new AliasMap());

        Assert.Null(results[0].Alias);
        Assert.Equal("[전화번호1]", results[1].Alias!.Value.Value); // still 1, not 2
    }

    [Fact]
    public void NeedsDecisionEarlierInText_DoesNotConsumeNumber()
    {
        var needsDecisionEarlier = Decision(PiiType.GpsCoordinate, "1.0,2.0", start: 0, CandidateDisposition.NeedsDecision);
        var protectLater = Decision(PiiType.Phone, "01011112222", start: 50, CandidateDisposition.Protect);

        var results = AliasAssigner.Assign([needsDecisionEarlier, protectLater], new AliasMap());

        Assert.Null(results[0].Alias);
        Assert.Equal("[전화번호1]", results[1].Alias!.Value.Value);
    }

    // ---- 22/23/24/25. output preservation: count, order, decisions/candidates unchanged ----
    [Fact]
    public void OutputPreservation_CountOrderAndValuesUnchanged()
    {
        var a = Decision(PiiType.Phone, "01011112222", start: 0, CandidateDisposition.Protect);
        var b = Decision(PiiType.Secret, "SYNTHETIC-SECRET", start: 20, CandidateDisposition.NeedsDecision);
        var c = Decision(PiiType.GpsCoordinate, "1.0,2.0", start: 40, CandidateDisposition.Bypass);

        var results = AliasAssigner.Assign([a, b, c], new AliasMap());

        Assert.Equal(3, results.Count); // 22
        Assert.Equal([a, b, c], results.Select(r => r.Decision)); // 23 order + 24/25 unchanged (full equality)
    }

    // ---- 26. AliasToken never carries the original canonical PII value as a separate field ----
    [Fact]
    public void AliasToken_DoesNotContainRawCanonicalValue()
    {
        var map = new AliasMap();
        const string canonicalPhone = "01099998888";
        var token = map.GetOrAdd(new CanonicalValue(PiiType.Phone, canonicalPhone));

        Assert.DoesNotContain(canonicalPhone, token.Value);
        // Structural check: AliasToken has exactly PiiType, Number, Value -- no 4th field.
        var fields = typeof(AliasToken).GetProperties();
        Assert.Equal(3, fields.Length);
    }

    // ---- 27. AliasMap/the whole Detection assembly has no dependency on Privon.Storage ----
    [Fact]
    public void DetectionAssembly_DoesNotReferenceStorage()
    {
        var referenced = typeof(AliasMap).Assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.DoesNotContain("Privon.Storage", referenced);
    }

    // ---- 28. no global/static mutable state -- two independent instances never share numbering ----
    [Fact]
    public void TwoIndependentAliasMapInstances_NeverShareState()
    {
        var mapA = new AliasMap();
        var mapB = new AliasMap();

        mapA.GetOrAdd(new CanonicalValue(PiiType.Phone, "01011112222"));
        mapA.GetOrAdd(new CanonicalValue(PiiType.Phone, "01033334444"));

        var firstInB = mapB.GetOrAdd(new CanonicalValue(PiiType.Phone, "01099998888"));
        Assert.Equal("[전화번호1]", firstInB.Value); // unaffected by mapA's state
    }

    // ---- 29. very large candidate list -> no exception ----
    [Fact]
    public void VeryLargeCandidateList_NoException()
    {
        var decisions = Enumerable.Range(0, 20_000)
            .Select(i => Decision(PiiType.Phone, $"CANONICAL-{i}", start: i, CandidateDisposition.Protect))
            .ToArray();

        var results = AliasAssigner.Assign(decisions, new AliasMap());

        Assert.Equal(20_000, results.Count);
        Assert.All(results, r => Assert.NotNull(r.Alias));
    }

    // Null argument fail-fast (part of "invalid state must fail fast, never fail-open").
    [Fact]
    public void NullDecisions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AliasAssigner.Assign(null!, new AliasMap()));
    }

    [Fact]
    public void NullAliasMap_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AliasAssigner.Assign([], null!));
    }

    // ---- 30. synthetic-only -- every value in this file is a hand-built filler fixture, never
    // real PII. ----
}
