using Privon.Core;
using Privon.Detection;
using Xunit;

namespace Privon.Detection.Tests;

/// <summary>
/// D3B0-B §26 -- the deterministic resolver R0-R6, through the production
/// <see cref="CandidateResolver"/> seam.
/// </summary>
public sealed class BridgeResolverTests
{
    // Injected candidates carry NO legacy DetectorId, so they are ContractDomain by EMISSION.
    private static DetectionCandidate Contract(
        string id, int start, int length, RiskLevel level, ResolutionEvidence evidence, int rank,
        PiiType type = PiiType.Secret, string? canonical = "v",
        DetectionConfidence confidence = DetectionConfidence.High,
        OwnerClaim owner = OwnerClaim.NotApplicable,
        AliasIdentity? alias = null, NormativeFieldBag? bag = null) =>
        new(type, new RawSpan(start, length), level, confidence,
            canonical is null ? null : new CanonicalValue(type, canonical),
            "harness",
            DetectorId.Create(id), evidence, rank, alias, owner, bag);

    private static ResolutionResult Resolve(int rawLength, params DetectionCandidate[] candidates) =>
        CandidateResolver.Resolve(new ResolutionInput(candidates, rawLength));

    // ---------------- R0 ----------------

    [Theory]
    [InlineData(-1, 5)]     // negative start
    [InlineData(0, 0)]      // zero length
    [InlineData(0, -3)]     // negative length
    [InlineData(21, 1)]     // start > N
    [InlineData(18, 5)]     // start + length > N
    [InlineData(20, 1)]     // start == N with positive length
    public void R0_InvalidSpan_FailsClosed(int start, int length)
    {
        var ex = Assert.Throws<InvalidCandidateSpanException>(() =>
            Resolve(20, Contract("a.one", start, length, RiskLevel.Level3, ResolutionEvidence.S, 50)));

        Assert.Equal("RESOLVER_INVALID_CANDIDATE_SPAN", ex.ErrorCode);
        Assert.Equal(FailureKind.ResolverSpanValidationFailure, ex.FailureKind);
        Assert.Equal(start, ex.InvalidSpanStart);
        Assert.Equal(length, ex.InvalidSpanLength);
        Assert.Equal(20, ex.RawInputLength);
        Assert.Equal(
            $"RESOLVER_INVALID_CANDIDATE_SPAN: start={start}; length={length}; rawInputLength=20",
            ex.Message);
    }

    [Fact]
    public void R0_IsOverflowSafe_NeverUses32BitAddition()
    {
        // Start + Length would overflow to a NEGATIVE number with 32-bit addition and could pass a
        // naive `Start + Length <= N` check. The overflow-safe form rejects it.
        var ex = Assert.Throws<InvalidCandidateSpanException>(() =>
            Resolve(20, Contract("a.one", 1, int.MaxValue, RiskLevel.Level3, ResolutionEvidence.S, 50)));
        Assert.Equal("RESOLVER_INVALID_CANDIDATE_SPAN", ex.ErrorCode);
    }

    [Fact]
    public void R0_SelectsTheLexicographicallySmallestViolatingSpan()
    {
        // CANONICAL_INVALID_SPAN_SELECTOR -- integers only, never the CCSK.
        var ex = Assert.Throws<InvalidCandidateSpanException>(() => Resolve(10,
            Contract("a.one", 50, 2, RiskLevel.Level3, ResolutionEvidence.S, 50),
            Contract("a.two", 12, 9, RiskLevel.Level3, ResolutionEvidence.S, 60),
            Contract("a.three", 12, 3, RiskLevel.Level3, ResolutionEvidence.S, 70)));

        Assert.Equal(12, ex.InvalidSpanStart);
        Assert.Equal(3, ex.InvalidSpanLength);
    }

    [Fact]
    public void R0_EmptyRawInputWithAnyCandidate_FailsClosed()
    {
        Assert.Throws<InvalidCandidateSpanException>(() =>
            Resolve(0, Contract("a.one", 0, 1, RiskLevel.Level3, ResolutionEvidence.S, 50)));
    }

    // ---------------- R0.5 ----------------

    [Fact]
    public void R05_Check1_UnknownNormativeField_FailsClosedWithConstantPayload()
    {
        var bag = NormativeFieldBag.Create([new("SomeUnknownField", 1)]);
        var ex = Assert.Throws<UnknownCandidateSemanticFieldException>(() =>
            Resolve(20, Contract("a.one", 0, 4, RiskLevel.Level3, ResolutionEvidence.S, 50, bag: bag)));

        Assert.Equal("RESOLVER_UNKNOWN_NORMATIVE_SEMANTIC_FIELD", ex.ErrorCode);
        Assert.Equal("RESOLVER_UNKNOWN_NORMATIVE_SEMANTIC_FIELD", ex.Message);
        // No field name, no candidate identity, no index, no value.
        Assert.DoesNotContain("SomeUnknownField", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void R05_Check2_UnknownEnumValue_FailsClosed()
    {
        var bad = Contract("a.one", 0, 4, (RiskLevel)99, ResolutionEvidence.S, 50);
        var ex = Assert.Throws<InvalidNormativeValueDomainException>(() => Resolve(20, bad));
        Assert.Equal("RESOLVER_UNKNOWN_NORMATIVE_ENUM_VALUE", ex.ErrorCode);
    }

    [Fact]
    public void R05_Check2_UnknownSemanticType_FailsClosed()
    {
        var bad = new DetectionCandidate((PiiType)999, new RawSpan(0, 4), RiskLevel.Level3,
            DetectionConfidence.High, null, "harness", DetectorId.Create("a.one"),
            ResolutionEvidence.S, 50);
        var ex = Assert.Throws<InvalidNormativeValueDomainException>(() => Resolve(20, bad));
        Assert.Equal("RESOLVER_UNKNOWN_NORMATIVE_ENUM_VALUE", ex.ErrorCode);
    }

    [Fact]
    public void R05_Check3_InvalidUnicode_FailsClosed_NoReplacementFallback()
    {
        string loneHighSurrogate = "abc\ud800";
        var bad = Contract("a.one", 0, 4, RiskLevel.Level3, ResolutionEvidence.S, 50, canonical: loneHighSurrogate);
        var ex = Assert.Throws<InvalidNormativeValueDomainException>(() => Resolve(20, bad));
        Assert.Equal("RESOLVER_INVALID_UNICODE_SCALAR_SEQUENCE", ex.ErrorCode);
    }

    [Fact]
    public void R05_ValidSupplementaryAndKoreanScalars_AreAccepted()
    {
        // NO_SILENT_UNICODE_POLICY_EXPANSION -- emoji, Korean and supplementary scalars stay valid.
        foreach (string good in new[] { "한국어", "日本語", "\U0001F600", "a\U00010000b", "" })
        {
            var c = Contract("a.one", 0, 4, RiskLevel.Level3, ResolutionEvidence.S, 50, canonical: good);
            var r = Resolve(20, c);
            Assert.Single(r.Winners);
        }
    }

    [Fact]
    public void R05_CheckPrecedence_Check1WinsOverCheck2()
    {
        // Each class is evaluated over the ENTIRE set before the next begins, so an unknown-field
        // candidate anywhere beats an enum violation anywhere.
        var bag = NormativeFieldBag.Create([new("Whatever", 1)]);
        Assert.Throws<UnknownCandidateSemanticFieldException>(() => Resolve(20,
            Contract("a.two", 6, 4, (RiskLevel)99, ResolutionEvidence.S, 60),
            Contract("a.one", 0, 4, RiskLevel.Level3, ResolutionEvidence.S, 50, bag: bag)));
    }

    [Fact]
    public void R05_AbsentCanonical_RunsNoCanonicalDomainCheck()
    {
        // §4.4 -- ABSENT means no CanonicalValue type or value-domain validation runs at all.
        var c = Contract("a.one", 0, 4, RiskLevel.Level3, ResolutionEvidence.S, 50, canonical: null);
        var r = Resolve(20, c);
        Assert.Single(r.Winners);
        Assert.Null(r.Winners[0].Canonical);
    }

    // ---------------- R1 ----------------

    [Fact]
    public void R1_ExactDuplicates_CollapseToOne_WithNoSuppressionRecord()
    {
        var a = Contract("a.one", 0, 4, RiskLevel.Level3, ResolutionEvidence.S, 50);
        var b = Contract("a.one", 0, 4, RiskLevel.Level3, ResolutionEvidence.S, 50);
        var r = Resolve(20, a, b);

        Assert.Single(r.Winners);
        Assert.Empty(r.Suppressed);
    }

    [Fact]
    public void R1_AbsentVsPresentCanonical_AreNotDuplicates()
    {
        // EXACT_DUPLICATE: canonical-different INCLUDING ABSENT vs PRESENT is NOT a duplicate.
        var present = Contract("a.one", 0, 4, RiskLevel.Level3, ResolutionEvidence.S, 50, canonical: "v");
        var absent = Contract("a.one", 0, 4, RiskLevel.Level3, ResolutionEvidence.S, 50, canonical: null);
        Assert.False(CandidateSemanticIdentity.AreExactDuplicates(present, absent));
    }

    [Fact]
    public void R1_AliasDifferentAndOwnerClaimDifferent_AreNotDuplicates()
    {
        var baseline = Contract("a.one", 0, 4, RiskLevel.Level3, ResolutionEvidence.S, 50);
        var aliased = Contract("a.one", 0, 4, RiskLevel.Level3, ResolutionEvidence.S, 50,
            alias: new AliasIdentity(AliasFamilyTable.FamilyOf(PiiType.Secret), 1));
        var owned = Contract("a.one", 0, 4, RiskLevel.Level3, ResolutionEvidence.S, 50, owner: OwnerClaim.True);

        Assert.False(CandidateSemanticIdentity.AreExactDuplicates(baseline, aliased));
        Assert.False(CandidateSemanticIdentity.AreExactDuplicates(baseline, owned));
    }

    // ---------------- R3 ----------------

    [Fact]
    public void R3_EqualKeyActualOverlap_FailsClosed_WithNoPartialOutput()
    {
        var u = Contract("a.one", 0, 10, RiskLevel.Level3, ResolutionEvidence.S, 50, canonical: "x");
        var v = Contract("a.two", 8, 10, RiskLevel.Level3, ResolutionEvidence.S, 50, canonical: "y");

        var ex = Assert.Throws<UnresolvableCandidateTieException>(() => Resolve(40, u, v));
        Assert.Equal("RESOLVER_AMBIGUOUS_EQUAL_KEY_OVERLAP", ex.ErrorCode);
        Assert.Equal(FailureKind.ResolverAmbiguityFailure, ex.FailureKind);
        Assert.Equal(ArbitrationDomain.ContractDomain, ex.ArbitrationDomain);
    }

    [Fact]
    public void R3_EqualKeyButNoActualOverlap_DoesNotFire()
    {
        // Adjacency is NOT overlap.
        var u = Contract("a.one", 0, 5, RiskLevel.Level3, ResolutionEvidence.S, 50, canonical: "x");
        var v = Contract("a.two", 5, 5, RiskLevel.Level3, ResolutionEvidence.S, 50, canonical: "y");
        var r = Resolve(40, u, v);
        Assert.Equal(2, r.Winners.Count);
    }

    [Fact]
    public void R3_CanonicalAmbiguousPairSelector_IsFrozenTestA()
    {
        // TEST A: A=[0,10), B=[8,18), C=[15,25), all equal-key. Pairs {A,B} at (8,2) and {B,C} at
        // (15,3); A and C do not overlap. Selected pair is {A,B}, payload span 8:2 -- identical
        // under every permutation.
        var a = Contract("a.aa", 0, 10, RiskLevel.Level3, ResolutionEvidence.S, 50, canonical: "a");
        var b = Contract("a.bb", 8, 10, RiskLevel.Level3, ResolutionEvidence.S, 50, canonical: "b");
        var c = Contract("a.cc", 15, 10, RiskLevel.Level3, ResolutionEvidence.S, 50, canonical: "c");

        foreach (var order in new[]
        {
            new[] { a, b, c }, new[] { c, b, a }, new[] { b, a, c },
            new[] { b, c, a }, new[] { a, c, b }, new[] { c, a, b },
        })
        {
            var ex = Assert.Throws<UnresolvableCandidateTieException>(() => Resolve(40, order));
            Assert.Equal(8, ex.SpanStart);
            Assert.Equal(2, ex.SpanLength);
            Assert.Equal(
                "RESOLVER_AMBIGUOUS_EQUAL_KEY_OVERLAP: domain=ContractDomain; span=8:2; low=a.aa; high=a.bb",
                ex.Message);
        }
    }

    // ---------------- R4 / R5 / R6 ----------------

    [Fact]
    public void R4_ComponentIsNotWinnerTakeAll()
    {
        // overlaps is not transitive, so one component may yield SEVERAL survivors. A
        // winner-take-all implementation would leave the independent remainder unprotected.
        var a = Contract("a.aa", 0, 10, RiskLevel.Level3, ResolutionEvidence.SCJP, 50, canonical: "a");
        var b = Contract("a.bb", 8, 4, RiskLevel.Level1, ResolutionEvidence.C, 60, canonical: "b");
        var c = Contract("a.cc", 11, 10, RiskLevel.Level3, ResolutionEvidence.SCJP, 70, canonical: "c");

        var r = Resolve(40, a, b, c);
        Assert.Equal(2, r.Winners.Count);
        Assert.Single(r.Suppressed);
        Assert.Equal("a.bb", r.Suppressed[0].Candidate.DetectorId.Value);
    }

    [Fact]
    public void R6_SuppressedOrdering_IsFrozenTestB()
    {
        // TEST B: W (us.atin, L3, S+C, [0,11), rank 50) survives; S1 (us.ssn, L3, S, rank 70,
        // canonical X) and S2 (us.ssn, L3, C, rank 70, canonical Y) are suppressed. R3 does not
        // fire. Keys 1-4 tie; key 5 decides at CCSK field 6 (C=1 < S=2). Order: S2 then S1.
        var w = Contract("us.atin", 0, 11, RiskLevel.Level3, ResolutionEvidence.SC, 50, canonical: "w");
        var s1 = Contract("us.ssn", 0, 9, RiskLevel.Level3, ResolutionEvidence.S, 70, canonical: "X");
        var s2 = Contract("us.ssn", 0, 9, RiskLevel.Level3, ResolutionEvidence.C, 70, canonical: "Y");

        var r = Resolve(40, s1, w, s2);

        Assert.Single(r.Winners);
        Assert.Equal("us.atin", r.Winners[0].DetectorId.Value);
        Assert.Equal(2, r.Suppressed.Count);
        Assert.Equal("Y", r.Suppressed[0].Candidate.Canonical!.Value.Value);   // S2 first
        Assert.Equal("X", r.Suppressed[1].Candidate.Canonical!.Value.Value);   // then S1
    }

    [Fact]
    public void R5_SuppressionProvenanceAndReason_AreDeterministic()
    {
        var strong = Contract("a.aa", 0, 10, RiskLevel.Level3, ResolutionEvidence.SCJP, 50, canonical: "a");
        var weak = Contract("a.bb", 2, 6, RiskLevel.Level1, ResolutionEvidence.C, 60, canonical: "b");

        var r = Resolve(40, strong, weak);
        var s = Assert.Single(r.Suppressed);

        Assert.Equal("a.bb", s.Candidate.DetectorId.Value);
        Assert.Equal("a.aa", s.SuppressedBy.DetectorId.Value);
        Assert.Equal(SuppressionReason.LowerLevel, s.Reason);
    }

    [Fact]
    public void Resolution_IsPermutationInvariant()
    {
        var a = Contract("a.aa", 0, 10, RiskLevel.Level3, ResolutionEvidence.SCJP, 50, canonical: "a");
        var b = Contract("a.bb", 2, 6, RiskLevel.Level1, ResolutionEvidence.C, 60, canonical: "b");
        var c = Contract("a.cc", 20, 5, RiskLevel.Level2, ResolutionEvidence.S, 70, canonical: "c");

        string Render(ResolutionResult r) =>
            string.Join("||", r.Winners.Select(CanonicalCandidateSerializationKey.Render))
            + "##" + string.Join("||", r.Suppressed.Select(x =>
                CanonicalCandidateSerializationKey.Render(x.Candidate) + ">" + x.Reason
                + ">" + CanonicalCandidateSerializationKey.Render(x.SuppressedBy)));

        string expected = Render(Resolve(40, a, b, c));
        foreach (var order in new[]
        {
            new[] { c, b, a }, new[] { b, a, c }, new[] { b, c, a },
            new[] { a, c, b }, new[] { c, a, b },
        })
            Assert.Equal(expected, Render(Resolve(40, order)));
    }

    [Fact]
    public void WinnerSelection_NeverDependsOnSourceOrRegistrationOrder()
    {
        // FORBIDDEN_HIDDEN_TIEBREAKERS -- two candidates differing only by rank must resolve the
        // same way whichever arrives first.
        var lowRank = Contract("a.aa", 0, 10, RiskLevel.Level3, ResolutionEvidence.S, 50, canonical: "a");
        var highRank = Contract("z.zz", 0, 10, RiskLevel.Level3, ResolutionEvidence.S, 999, canonical: "z");

        var forward = Resolve(40, lowRank, highRank);
        var backward = Resolve(40, highRank, lowRank);

        Assert.Equal("a.aa", Assert.Single(forward.Winners).DetectorId.Value);
        Assert.Equal("a.aa", Assert.Single(backward.Winners).DetectorId.Value);
    }

    [Fact]
    public void FailureUnion_IsExactlyThreeKindsFiveVariantsFourExceptionTypes()
    {
        Assert.Equal(3, Enum.GetValues<FailureKind>().Length);

        // The four resolver exception types, and their five (kind, code) variants.
        var variants = new HashSet<string>(StringComparer.Ordinal)
        {
            FailureKind.ResolverSpanValidationFailure + "|" + InvalidCandidateSpanException.Code,
            FailureKind.ResolverDomainValidationFailure + "|" + UnknownCandidateSemanticFieldException.Code,
            FailureKind.ResolverDomainValidationFailure + "|" + InvalidNormativeValueDomainException.UnknownEnumValueCode,
            FailureKind.ResolverDomainValidationFailure + "|" + InvalidNormativeValueDomainException.InvalidUnicodeCode,
            FailureKind.ResolverAmbiguityFailure + "|" + UnresolvableCandidateTieException.Code,
        };
        Assert.Equal(5, variants.Count);
    }
}
