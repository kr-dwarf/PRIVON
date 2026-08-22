using Privon.Core;
using Privon.Detection;

namespace Privon.Detection.Tests;

// Phase 2R.4 — ExceptionTrustedEvaluator tests. All candidates/entries are built by hand (same
// style as OverlapResolverTests/ContextRiskAdjusterTests) so exact-match / RiskLevel-branch /
// TYPED_VALUE_PIITYPE_INVARIANT behavior can be pinned precisely without depending on any real
// detector. No Storage types are used anywhere in this file (the Storage bridge is a later,
// separate phase). All values are synthetic filler, never real PII.
public class ExceptionTrustedEvaluatorTests
{
    private static DetectionCandidate Candidate(
        PiiType type, RiskLevel risk, string canonicalValue,
        DetectionConfidence conf = DetectionConfidence.High, int start = 0, int length = 5, string detector = "Test") =>
        new(type, new RawSpan(start, length), risk, conf, new CanonicalValue(type, canonicalValue), detector);

    private static DetectionResult Result(params DetectionCandidate[] candidates) => new(candidates);

    private static AmbiguousExceptionValue Exception(PiiType type, string value) => new(type, new CanonicalValue(type, value));
    private static TrustedPublicValue Trusted(PiiType type, string value) => new(type, new CanonicalValue(type, value));

    private static IReadOnlyList<AmbiguousExceptionValue> NoExceptions => [];
    private static IReadOnlyList<TrustedPublicValue> NoTrusted => [];

    // ---- 1. Level1 + matching AmbiguousException -> Trusted ----
    [Fact]
    public void Level1_MatchingException_Trusted()
    {
        var candidate = Candidate(PiiType.GpsCoordinate, RiskLevel.Level1, "37.5,127.0");
        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidate),
            [Exception(PiiType.GpsCoordinate, "37.5,127.0")], NoTrusted);

        Assert.Equal(TrustState.Trusted, result[0].TrustState);
    }

    // ---- 2. Level1 + no match -> Untrusted ----
    [Fact]
    public void Level1_NoMatch_Untrusted()
    {
        var candidate = Candidate(PiiType.GpsCoordinate, RiskLevel.Level1, "37.5,127.0");
        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidate), NoExceptions, NoTrusted);

        Assert.Equal(TrustState.Untrusted, result[0].TrustState);
    }

    // ---- 3. Level1 + matching TrustedPublic ONLY (not in exceptions) -> Untrusted ----
    [Fact]
    public void Level1_MatchingTrustedPublicOnly_StillUntrusted()
    {
        var candidate = Candidate(PiiType.GpsCoordinate, RiskLevel.Level1, "37.5,127.0");
        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidate),
            NoExceptions, [Trusted(PiiType.GpsCoordinate, "37.5,127.0")]);

        Assert.Equal(TrustState.Untrusted, result[0].TrustState);
    }

    // ---- 4. Level2 + matching TrustedPublic -> Trusted ----
    [Fact]
    public void Level2_MatchingTrustedPublic_Trusted()
    {
        var candidate = Candidate(PiiType.Phone, RiskLevel.Level2, "01012345678");
        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidate),
            NoExceptions, [Trusted(PiiType.Phone, "01012345678")]);

        Assert.Equal(TrustState.Trusted, result[0].TrustState);
    }

    // ---- 5. Level2 + no match -> Untrusted ----
    [Fact]
    public void Level2_NoMatch_Untrusted()
    {
        var candidate = Candidate(PiiType.Phone, RiskLevel.Level2, "01012345678");
        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidate), NoExceptions, NoTrusted);

        Assert.Equal(TrustState.Untrusted, result[0].TrustState);
    }

    // ---- 6. Level2 + matching AmbiguousException ONLY (not in trustedPublic) -> Untrusted ----
    [Fact]
    public void Level2_MatchingExceptionOnly_StillUntrusted()
    {
        var candidate = Candidate(PiiType.Phone, RiskLevel.Level2, "01012345678");
        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidate),
            [Exception(PiiType.Phone, "01012345678")], NoTrusted);

        Assert.Equal(TrustState.Untrusted, result[0].TrustState);
    }

    // ---- 7/8/9. Level3 never Trusted, even with matching entries in one or both lists ----
    [Fact]
    public void Level3_MatchingException_Untrusted()
    {
        var candidate = Candidate(PiiType.Secret, RiskLevel.Level3, "SYNTHETIC-SECRET");
        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidate),
            [Exception(PiiType.Secret, "SYNTHETIC-SECRET")], NoTrusted);

        Assert.Equal(TrustState.Untrusted, result[0].TrustState);
    }

    [Fact]
    public void Level3_MatchingTrusted_Untrusted()
    {
        var candidate = Candidate(PiiType.Secret, RiskLevel.Level3, "SYNTHETIC-SECRET");
        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidate),
            NoExceptions, [Trusted(PiiType.Secret, "SYNTHETIC-SECRET")]);

        Assert.Equal(TrustState.Untrusted, result[0].TrustState);
    }

    [Fact]
    public void Level3_MatchingBothLists_StillUntrusted()
    {
        var candidate = Candidate(PiiType.Secret, RiskLevel.Level3, "SYNTHETIC-SECRET");
        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidate),
            [Exception(PiiType.Secret, "SYNTHETIC-SECRET")], [Trusted(PiiType.Secret, "SYNTHETIC-SECRET")]);

        Assert.Equal(TrustState.Untrusted, result[0].TrustState);
    }

    // ---- 10. outer PiiType != CanonicalValue.PiiType exception entry -> ignored, Untrusted ----
    [Fact]
    public void MalformedException_OuterInnerMismatch_IgnoredNotThrown()
    {
        var candidate = Candidate(PiiType.Phone, RiskLevel.Level1, "01012345678");
        // Outer says Phone, but the CanonicalValue inside is stamped Email -- inconsistent.
        var malformed = new AmbiguousExceptionValue(PiiType.Phone, new CanonicalValue(PiiType.Email, "01012345678"));

        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidate), [malformed], NoTrusted);

        Assert.Equal(TrustState.Untrusted, result[0].TrustState);
    }

    // ---- 11. outer PiiType != CanonicalValue.PiiType trusted entry -> ignored, Untrusted ----
    [Fact]
    public void MalformedTrusted_OuterInnerMismatch_IgnoredNotThrown()
    {
        var candidate = Candidate(PiiType.Phone, RiskLevel.Level2, "01012345678");
        var malformed = new TrustedPublicValue(PiiType.Phone, new CanonicalValue(PiiType.Email, "01012345678"));

        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidate), NoExceptions, [malformed]);

        Assert.Equal(TrustState.Untrusted, result[0].TrustState);
    }

    // ---- 12. candidate PiiType != entry PiiType -> Untrusted ----
    [Fact]
    public void DifferentPiiType_SameRawLookingValue_Untrusted()
    {
        var candidate = Candidate(PiiType.BankAccountNumber, RiskLevel.Level2, "01012345678");
        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidate),
            NoExceptions, [Trusted(PiiType.Phone, "01012345678")]);

        Assert.Equal(TrustState.Untrusted, result[0].TrustState);
    }

    // ---- 13. same type, canonical value differs -> Untrusted ----
    [Fact]
    public void SameType_DifferentCanonicalValue_Untrusted()
    {
        var candidate = Candidate(PiiType.Phone, RiskLevel.Level2, "01012345678");
        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidate),
            NoExceptions, [Trusted(PiiType.Phone, "01099999999")]);

        Assert.Equal(TrustState.Untrusted, result[0].TrustState);
    }

    // ---- 14. exact canonical equality is sufficient (positive control) ----
    [Fact]
    public void ExactCanonicalEquality_IsSufficient()
    {
        var candidate = Candidate(PiiType.Email, RiskLevel.Level2, "user@example.com");
        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidate),
            NoExceptions, [Trusted(PiiType.Email, "user@example.com")]);

        Assert.Equal(TrustState.Trusted, result[0].TrustState);
    }

    // ---- 15. prefix non-match ----
    [Fact]
    public void PrefixMatch_IsNotAMatch()
    {
        var candidate = Candidate(PiiType.Phone, RiskLevel.Level2, "0311234567");
        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidate),
            NoExceptions, [Trusted(PiiType.Phone, "031")]);

        Assert.Equal(TrustState.Untrusted, result[0].TrustState);
    }

    // ---- 16. substring non-match ----
    [Fact]
    public void SubstringMatch_IsNotAMatch()
    {
        var candidate = Candidate(PiiType.Email, RiskLevel.Level2, "user@example.com");
        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidate),
            NoExceptions, [Trusted(PiiType.Email, "example.com")]);

        Assert.Equal(TrustState.Untrusted, result[0].TrustState);
    }

    // ---- 17. duplicate matching entries -> still exactly one evaluated candidate, Trusted ----
    [Fact]
    public void DuplicateMatchingEntries_OneTrustedResultOnly()
    {
        var candidate = Candidate(PiiType.Phone, RiskLevel.Level2, "01012345678");
        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidate),
            NoExceptions, [Trusted(PiiType.Phone, "01012345678"), Trusted(PiiType.Phone, "01012345678")]);

        Assert.Single(result);
        Assert.Equal(TrustState.Trusted, result[0].TrustState);
    }

    // ---- 18. multiple candidates independently evaluated (design doc decision 75) ----
    [Fact]
    public void MultipleCandidates_IndependentlyEvaluated()
    {
        var storePhone = Candidate(PiiType.Phone, RiskLevel.Level2, "0311112222", start: 0, length: 10);
        var customerPhone = Candidate(PiiType.Phone, RiskLevel.Level2, "01033334444", start: 20, length: 11);
        var trustedGps = Candidate(PiiType.GpsCoordinate, RiskLevel.Level1, "37.5,127.0", start: 40, length: 10);
        var randomGps = Candidate(PiiType.GpsCoordinate, RiskLevel.Level1, "10.0,20.0", start: 60, length: 9);
        var secret = Candidate(PiiType.Secret, RiskLevel.Level3, "SYNTHETIC-SECRET", start: 80, length: 16);

        var result = ExceptionTrustedEvaluator.Evaluate(
            Result(storePhone, customerPhone, trustedGps, randomGps, secret),
            [Exception(PiiType.GpsCoordinate, "37.5,127.0")],
            [Trusted(PiiType.Phone, "0311112222")]);

        Assert.Equal(TrustState.Trusted, result[0].TrustState);   // storePhone
        Assert.Equal(TrustState.Untrusted, result[1].TrustState); // customerPhone -- same PiiType, not the trusted one
        Assert.Equal(TrustState.Trusted, result[2].TrustState);   // trustedGps
        Assert.Equal(TrustState.Untrusted, result[3].TrustState); // randomGps -- same PiiType, not excepted
        Assert.Equal(TrustState.Untrusted, result[4].TrustState); // secret -- Level3, always untrusted
    }

    // ---- 19-26. candidate preservation invariants: count, order, and every field unchanged ----
    [Fact]
    public void CandidatePreservation_CountOrderAndAllFieldsUnchanged()
    {
        var a = Candidate(PiiType.Phone, RiskLevel.Level2, "01012345678", DetectionConfidence.High, start: 0, length: 11, detector: "PhoneDetector");
        var b = Candidate(PiiType.Secret, RiskLevel.Level3, "SYNTHETIC-SECRET", DetectionConfidence.Medium, start: 20, length: 16, detector: "SecretDetector");
        var c = Candidate(PiiType.GpsCoordinate, RiskLevel.Level1, "10.0,20.0", DetectionConfidence.Low, start: 40, length: 9, detector: "GpsCoordinateDetector");

        var result = ExceptionTrustedEvaluator.Evaluate(Result(a, b, c),
            [Exception(PiiType.GpsCoordinate, "10.0,20.0")], [Trusted(PiiType.Phone, "01012345678")]);

        Assert.Equal(3, result.Count); // 19
        Assert.Equal([a, b, c], result.Select(r => r.Candidate)); // 20 order + 21 record unchanged (full equality)

        Assert.Equal(a.RiskLevel, result[0].Candidate.RiskLevel); // 22
        Assert.Equal(a.Confidence, result[0].Candidate.Confidence); // 23
        Assert.Equal(a.Span, result[0].Candidate.Span); // 24
        Assert.Equal(a.Canonical, result[0].Candidate.Canonical); // 25
        Assert.Equal(a.DetectorName, result[0].Candidate.DetectorName); // 26
    }

    // ---- 27. empty exception/trusted lists -> all Untrusted ----
    [Fact]
    public void EmptyLists_AllUntrusted()
    {
        var candidates = new[]
        {
            Candidate(PiiType.Phone, RiskLevel.Level2, "01012345678"),
            Candidate(PiiType.GpsCoordinate, RiskLevel.Level1, "10.0,20.0", start: 20, length: 9),
            Candidate(PiiType.Secret, RiskLevel.Level3, "SYNTHETIC-SECRET", start: 40, length: 16),
        };

        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidates), NoExceptions, NoTrusted);

        Assert.All(result, r => Assert.Equal(TrustState.Untrusted, r.TrustState));
    }

    // ---- 28. null collection fail-fast ----
    [Fact]
    public void NullDetectionResult_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ExceptionTrustedEvaluator.Evaluate(null!, NoExceptions, NoTrusted));
    }

    [Fact]
    public void NullExceptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ExceptionTrustedEvaluator.Evaluate(Result(), null!, NoTrusted));
    }

    [Fact]
    public void NullTrustedPublic_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ExceptionTrustedEvaluator.Evaluate(Result(), NoExceptions, null!));
    }

    // ---- 29. property/fuzz: no generated entry list combination ever makes Level3 Trusted ----
    [Fact]
    public void Level3_Fuzz_NeverTrusted()
    {
        var random = new Random(20260815);
        var piiTypes = Enum.GetValues<PiiType>();

        for (int iteration = 0; iteration < 500; iteration++)
        {
            var type = piiTypes[random.Next(piiTypes.Length)];
            var value = "SYNTHETIC-" + random.Next(1_000_000);
            var candidate = Candidate(type, RiskLevel.Level3, value);

            // Randomly generated entries, some intentionally matching, some malformed
            // (outer/inner mismatch), fed into BOTH lists.
            var exceptions = new List<AmbiguousExceptionValue>();
            var trusted = new List<TrustedPublicValue>();
            for (int i = 0; i < random.Next(0, 5); i++)
            {
                exceptions.Add(random.Next(2) == 0
                    ? new AmbiguousExceptionValue(type, new CanonicalValue(type, value))
                    : new AmbiguousExceptionValue(type, new CanonicalValue(piiTypes[random.Next(piiTypes.Length)], value)));
                trusted.Add(random.Next(2) == 0
                    ? new TrustedPublicValue(type, new CanonicalValue(type, value))
                    : new TrustedPublicValue(type, new CanonicalValue(piiTypes[random.Next(piiTypes.Length)], value)));
            }

            var result = ExceptionTrustedEvaluator.Evaluate(Result(candidate), exceptions, trusted);
            Assert.Equal(TrustState.Untrusted, result[0].TrustState);
        }
    }

    // ---- 30. all current PiiTypes as synthetic candidates -> no evaluator exception ----
    [Fact]
    public void AllCurrentPiiTypes_NoException()
    {
        var piiTypes = Enum.GetValues<PiiType>();
        var candidates = piiTypes.Select((t, i) => Candidate(t, RiskLevel.Level2, $"VALUE-{i}", start: i * 10, length: 5)).ToArray();

        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidates), NoExceptions, NoTrusted);

        Assert.Equal(piiTypes.Length, result.Count);
    }

    // ---- 31. very large candidate list -> no exception/index error ----
    [Fact]
    public void VeryLargeCandidateList_NoException()
    {
        var candidates = Enumerable.Range(0, 20_000)
            .Select(i => Candidate(PiiType.Phone, RiskLevel.Level2, $"VALUE-{i}", start: i, length: 1))
            .ToArray();

        var result = ExceptionTrustedEvaluator.Evaluate(Result(candidates),
            NoExceptions, [Trusted(PiiType.Phone, "VALUE-5")]);

        Assert.Equal(20_000, result.Count);
        Assert.Equal(TrustState.Trusted, result[5].TrustState);
        Assert.Equal(TrustState.Untrusted, result[6].TrustState);
    }

    // ---- 32. synthetic fixture only -- every value in this file is a hand-built filler
    // fixture, never real PII. ----
}
