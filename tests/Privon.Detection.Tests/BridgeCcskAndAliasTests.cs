using System.Globalization;
using Privon.Core;
using Privon.Detection;
using Xunit;

namespace Privon.Detection.Tests;

/// <summary>D3B0-B §3 / §10 -- the CCSK and the neutral alias identity.</summary>
public sealed class BridgeCcskAndAliasTests
{
    private static DetectionCandidate C(
        string id = "a.one", int start = 0, int length = 4, RiskLevel level = RiskLevel.Level2,
        ResolutionEvidence evidence = ResolutionEvidence.S, int rank = 50,
        PiiType type = PiiType.Secret, string? canonical = "v",
        DetectionConfidence confidence = DetectionConfidence.High,
        AliasIdentity? alias = null, OwnerClaim owner = OwnerClaim.NotApplicable) =>
        new(type, new RawSpan(start, length), level, confidence,
            canonical is null ? null : new CanonicalValue(type, canonical), "harness",
            DetectorId.Create(id), evidence, rank, alias, owner);

    [Fact]
    public void Ccsk_AbsentCanonicalSortsStrictlyBeforePresent()
    {
        var absent = C(canonical: null);
        var present = C(canonical: "a");
        Assert.True(CanonicalCandidateSerializationKey.Compare(absent, present) < 0);
        Assert.True(CanonicalCandidateSerializationKey.Compare(present, absent) > 0);
    }

    [Fact]
    public void Ccsk_AbsentAliasSortsStrictlyBeforePresent()
    {
        var absent = C(alias: null);
        var present = C(alias: new AliasIdentity(AliasFamilyTable.FamilyOf(PiiType.Secret), 1));
        Assert.True(CanonicalCandidateSerializationKey.Compare(absent, present) < 0);
    }

    [Fact]
    public void Ccsk_IsStableAndTotalOverTheElevenFields()
    {
        var a = C(id: "a.one");
        var b = C(id: "a.two");

        Assert.Equal(0, CanonicalCandidateSerializationKey.Compare(a, C(id: "a.one")));
        Assert.True(CanonicalCandidateSerializationKey.Compare(a, b) < 0);
        Assert.True(CanonicalCandidateSerializationKey.Compare(b, a) > 0);
    }

    [Fact]
    public void Ccsk_UsesFrozenPositionsNotUnderlyingEnumValues()
    {
        // Field 5: Level1 < Level2 < Level3 by frozen position.
        Assert.True(CanonicalCandidateSerializationKey.Compare(
            C(level: RiskLevel.Level1), C(level: RiskLevel.Level3)) < 0);
        // Field 6: C(1) < S(2) -- the key that decides frozen TEST B.
        Assert.True(CanonicalCandidateSerializationKey.Compare(
            C(evidence: ResolutionEvidence.C), C(evidence: ResolutionEvidence.S)) < 0);
        // Field 7: Low < Medium < High.
        Assert.True(CanonicalCandidateSerializationKey.Compare(
            C(confidence: DetectionConfidence.Low), C(confidence: DetectionConfidence.High)) < 0);
    }

    [Fact]
    public void Ccsk_SupplementaryScalarSortsAfterEveryBmpScalar()
    {
        // The reason string.CompareOrdinal is FORBIDDEN: it compares UTF-16 code units and would
        // place U+E000..U+FFFF AFTER a supplementary scalar. UTF-8 byte order does not.
        var bmp = C(canonical: "\uFFFD");
        var supplementary = C(canonical: "\U0001F600");

        Assert.True(CanonicalCandidateSerializationKey.Compare(bmp, supplementary) < 0);
        Assert.True(string.CompareOrdinal("\uFFFD", "\U0001F600") > 0);   // the trap, demonstrated
    }

    [Fact]
    public void Ccsk_RenderIsInvariantAcrossCultures()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            string? first = null;
            foreach (string culture in new[] { "en-US", "de-DE", "ar-SA", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                string rendered = CanonicalCandidateSerializationKey.Render(C(start: 7, rank: -3));
                first ??= rendered;
                Assert.Equal(first, rendered);
                Assert.DoesNotContain("\u061C", rendered, StringComparison.Ordinal);
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void AliasIdentity_EqualityUsesOnlyFamilyAndOccurrence()
    {
        var family = AliasFamilyTable.FamilyOf(PiiType.UsSsn);
        Assert.Equal(new AliasIdentity(family, 1), new AliasIdentity(family, 1));
        Assert.NotEqual(new AliasIdentity(family, 1), new AliasIdentity(family, 2));
        Assert.NotEqual(new AliasIdentity(family, 1),
                        new AliasIdentity(AliasFamilyTable.FamilyOf(PiiType.UsItin), 1));
    }

    [Fact]
    public void CanonicalContractToken_ReproducesTheFrozenBytes()
    {
        (PiiType Type, int Occurrence, string Token)[] frozen =
        [
            (PiiType.UsSsn, 1, "[SSN1]"),
            (PiiType.UsSsn, 2, "[SSN2]"),
            (PiiType.UsItin, 1, "[ITIN1]"),
            (PiiType.UsAtin, 1, "[ATIN1]"),
            (PiiType.UsMbi, 1, "[MBI1]"),
            (PiiType.UsSevis, 1, "[SEVIS1]"),
            (PiiType.UsANumber, 1, "[A-NUMBER1]"),
            (PiiType.UsI94, 1, "[I-941]"),
            (PiiType.UsPassport, 1, "[여권번호1]"),
            (PiiType.UsDriverStateId, 1, "[운전면허번호1]"),
            (PiiType.Name, 1, "[이름1]"),
            (PiiType.Address, 1, "[주소1]"),
            (PiiType.BankAccount, 1, "[계좌1]"),
            (PiiType.Phone, 1, "[전화번호1]"),
            (PiiType.Phone, 2, "[전화번호2]"),
            (PiiType.Email, 1, "[이메일1]"),
        ];

        foreach (var (type, occurrence, expected) in frozen)
        {
            var identity = new AliasIdentity(AliasFamilyTable.FamilyOf(type), occurrence);
            Assert.Equal(expected, identity.CanonicalContractToken);
            Assert.Equal(expected, identity.ToString());   // §22.6 -- one interpretation
        }
    }

    [Fact]
    public void CcskAliasOnly_FrozenByteOrderHolds()
    {
        // [SSN1] = 5B 53 53 4E 31 5D < [SSN2] = 5B 53 53 4E 32 5D
        var family = AliasFamilyTable.FamilyOf(PiiType.UsSsn);
        var one = C(alias: new AliasIdentity(family, 1));
        var two = C(alias: new AliasIdentity(family, 2));
        Assert.True(CanonicalCandidateSerializationKey.Compare(one, two) < 0);
    }

    [Fact]
    public void CanonicalContractToken_IsIndependentOfAmbientCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            var identity = new AliasIdentity(AliasFamilyTable.FamilyOf(PiiType.UsSsn), 12);
            foreach (string culture in new[] { "en-US", "de-DE", "ar-SA", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Assert.Equal("[SSN12]", identity.CanonicalContractToken);
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void Renderer_IsPresentationOnly_AndNeverAffectsIdentity()
    {
        var identity = new AliasIdentity(AliasFamilyTable.FamilyOf(PiiType.UsSsn), 3);

        // D3B0 ships exactly one renderer; it returns the canonical contract token verbatim.
        var rendererType = typeof(AliasIdentity).Assembly
            .GetType("Privon.Detection.ContractDefaultAliasTokenRenderer");
        Assert.NotNull(rendererType);

        var renderer = (IAliasTokenRenderer)Activator.CreateInstance(rendererType!, nonPublic: true)!;
        Assert.Equal(identity.CanonicalContractToken,
                     renderer.Render(identity, ReplacementLanguage.ContractDefault));

        // ReplacementLanguage stays closed at one member until an audited amendment.
        Assert.Single(Enum.GetValues<ReplacementLanguage>());
    }
}
