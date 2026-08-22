using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// Phase 2P — Detection Pipeline Foundation integration tests. Exercises DetectionPipeline
// (NormalizedView -> registered detectors -> OverlapResolver -> DetectionResult) end to end.
// No Trusted/Exception/Alias/replacement here -- that is explicitly out of scope for this
// phase. All fixtures are synthetic.
public class DetectionPipelineTests
{
    // A minimal test-only detector that returns whatever the caller configured, so overlap /
    // ordering / failure-isolation scenarios can be engineered precisely without depending on
    // real detectors' exact regex behavior.
    private sealed class FakeDetector : IDetector
    {
        private readonly Func<DetectionContext, IReadOnlyList<DetectionCandidate>> _detect;
        public string Name { get; }
        public PiiType PiiType { get; }

        public FakeDetector(string name, PiiType piiType, Func<DetectionContext, IReadOnlyList<DetectionCandidate>> detect)
        {
            Name = name;
            PiiType = piiType;
            _detect = detect;
        }

        public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context) => _detect(context);
    }

    private sealed class ThrowingDetector : IDetector
    {
        public string Name => "ThrowingDetector";
        public PiiType PiiType => PiiType.Secret;
        public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context) =>
            throw new InvalidOperationException("synthetic detector failure");
    }

    private static DetectionCandidate Candidate(int start, int length, RiskLevel risk, DetectionConfidence conf, PiiType type, string detectorName) =>
        new(type, new RawSpan(start, length), risk, conf, new CanonicalValue(type, $"v{start}"), detectorName);

    // Synthetic, clearly non-issuer prefix -- deterministic Luhn-completion generator, same
    // approach used in UrlContextRegressionTests / FilenameContextRegressionTests.
    private const string SyntheticCardPrefix15 = "246813579024681";

    private static string BuildLuhnValidCard(string first15Digits)
    {
        int sum = 0;
        bool doubleDigit = true;
        for (int i = first15Digits.Length - 1; i >= 0; i--)
        {
            int d = first15Digits[i] - '0';
            if (doubleDigit)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            doubleDigit = !doubleDigit;
        }
        int checkDigit = (10 - (sum % 10)) % 10;
        return first15Digits + checkDigit;
    }

    // ---- 1. empty string -> no results ----
    [Fact]
    public void EmptyString_NoResults()
    {
        var result = DetectionPipeline.CreateDefault().Detect("");
        Assert.Empty(result.Candidates);
    }

    // ---- 2. single phone ----
    [Fact]
    public void SinglePhone_ReturnsPhoneCandidate()
    {
        var result = DetectionPipeline.CreateDefault().Detect("연락처: 010-1234-5678 입니다.");
        Assert.Single(result.Candidates);
        Assert.Equal(PiiType.Phone, result.Candidates[0].PiiType);
    }

    // ---- 3. single email ----
    [Fact]
    public void SingleEmail_ReturnsEmailCandidate()
    {
        var result = DetectionPipeline.CreateDefault().Detect("이메일: user@example.com 입니다.");
        Assert.Single(result.Candidates);
        Assert.Equal(PiiType.Email, result.Candidates[0].PiiType);
    }

    // ---- 4. single Level3 (RRN) ----
    [Fact]
    public void SingleRrn_ReturnsLevel3Candidate()
    {
        var result = DetectionPipeline.CreateDefault().Detect("주민번호: 900101-1234568 입니다.");
        Assert.Single(result.Candidates);
        Assert.Equal(RiskLevel.Level3, result.Candidates[0].RiskLevel);
    }

    // ---- 5. eight different PII types in one sentence -> all returned ----
    [Fact]
    public void MixedSentence_AllEightPiiTypesReturned()
    {
        var digits16 = BuildLuhnValidCard(SyntheticCardPrefix15);
        var card = $"{digits16[..4]}-{digits16[4..8]}-{digits16[8..12]}-{digits16[12..]}";
        var rawText =
            "연락처: 010-1234-5678, 이메일: user@example.com, 주민번호: 900101-1234568, " +
            $"카드번호: {card}, api_key=SyntheticSecretValue123, " +
            "IP: 203.0.113.42, MAC: 02:00:00:00:00:01, lat=37.5665, lon=126.9780";

        var result = DetectionPipeline.CreateDefault().Detect(rawText);

        var types = result.Candidates.Select(c => c.PiiType).ToHashSet();
        Assert.Equal(
            new HashSet<PiiType>
            {
                PiiType.Phone, PiiType.Email, PiiType.ResidentRegistrationNumber, PiiType.CardNumber,
                PiiType.Secret, PiiType.IpAddress, PiiType.MacAddress, PiiType.GpsCoordinate,
            },
            types);
        Assert.Equal(8, result.Candidates.Count);
    }

    // ---- 6. non-overlapping candidates all kept ----
    [Fact]
    public void NonOverlappingCandidates_AllKept()
    {
        IDetector a = new FakeDetector("A", PiiType.Phone, _ => [Candidate(0, 5, RiskLevel.Level2, DetectionConfidence.High, PiiType.Phone, "A")]);
        IDetector b = new FakeDetector("B", PiiType.Email, _ => [Candidate(10, 5, RiskLevel.Level2, DetectionConfidence.High, PiiType.Email, "B")]);

        var pipeline = new DetectionPipeline([a, b]);
        var result = pipeline.Detect("0123456789012345");

        Assert.Equal(2, result.Candidates.Count);
    }

    // ---- 7. intentionally overlapping synthetic candidates -> resolved to one, per existing
    // OverlapResolver policy (higher RiskLevel wins) ----
    [Fact]
    public void OverlappingCandidates_ResolvedToOne_ByExistingPolicy()
    {
        IDetector low = new FakeDetector("Low", PiiType.Phone, _ => [Candidate(0, 10, RiskLevel.Level2, DetectionConfidence.High, PiiType.Phone, "Low")]);
        IDetector high = new FakeDetector("High", PiiType.Secret, _ => [Candidate(2, 4, RiskLevel.Level3, DetectionConfidence.Low, PiiType.Secret, "High")]);

        var pipeline = new DetectionPipeline([low, high]);
        var result = pipeline.Detect("0123456789012345");

        Assert.Single(result.Candidates);
        Assert.Equal(RiskLevel.Level3, result.Candidates[0].RiskLevel);
    }

    // ---- 8. detector registration order does not affect the final resolved result, even in
    // an exact RiskLevel/Confidence/span-length tie ----
    [Fact]
    public void DetectorRegistrationOrder_DoesNotAffectResolvedResult_EvenOnExactTie()
    {
        IDetector phoneLike = new FakeDetector("PhoneLike", PiiType.Phone, _ => [Candidate(0, 5, RiskLevel.Level2, DetectionConfidence.High, PiiType.Phone, "PhoneLike")]);
        IDetector emailLike = new FakeDetector("EmailLike", PiiType.Email, _ => [Candidate(0, 5, RiskLevel.Level2, DetectionConfidence.High, PiiType.Email, "EmailLike")]);

        var forward = new DetectionPipeline([phoneLike, emailLike]).Detect("0123456789012345");
        var reversed = new DetectionPipeline([emailLike, phoneLike]).Detect("0123456789012345");

        Assert.Single(forward.Candidates);
        Assert.Single(reversed.Candidates);
        Assert.Equal(forward.Candidates[0].PiiType, reversed.Candidates[0].PiiType);
    }

    [Fact]
    public void RealDetectors_ReorderedRegistration_SameResolvedResult()
    {
        var rawText = "연락처: 010-1234-5678, 이메일: user@example.com, IP: 203.0.113.42";

        IReadOnlyList<IDetector> orderA = [new PhoneDetector(), new EmailDetector(), new IpAddressDetector()];
        IReadOnlyList<IDetector> orderB = [new IpAddressDetector(), new EmailDetector(), new PhoneDetector()];

        var resultA = new DetectionPipeline(orderA).Detect(rawText);
        var resultB = new DetectionPipeline(orderB).Detect(rawText);

        var setA = resultA.Candidates.Select(c => (c.PiiType, c.Span)).OrderBy(t => t.Span.Start).ToList();
        var setB = resultB.Candidates.Select(c => (c.PiiType, c.Span)).OrderBy(t => t.Span.Start).ToList();
        Assert.Equal(setA, setB);
    }

    // ---- 9. one detector returning zero candidates does not affect the others ----
    [Fact]
    public void DetectorReturningNoCandidates_DoesNotAffectOthers()
    {
        IDetector empty = new FakeDetector("Empty", PiiType.Secret, _ => []);
        var pipeline = new DetectionPipeline([empty, new PhoneDetector()]);

        var result = pipeline.Detect("연락처: 010-1234-5678 입니다.");
        Assert.Single(result.Candidates);
        Assert.Equal(PiiType.Phone, result.Candidates[0].PiiType);
    }

    // ---- 10. the same detector returning multiple candidates -> all processed ----
    [Fact]
    public void SameDetector_MultipleCandidates_AllProcessed()
    {
        var result = DetectionPipeline.CreateDefault().Detect("연락처1: 010-1234-5678, 연락처2: 010-8765-4321");
        Assert.Equal(2, result.Candidates.Count);
        Assert.All(result.Candidates, c => Assert.Equal(PiiType.Phone, c.PiiType));
    }

    // ---- 11. already-protected alias tokens mixed in -> no re-detection, per each existing
    // detector's own policy (the pipeline adds no new alias awareness of its own) ----
    [Fact]
    public void AlreadyAliasedTokens_MixedTogether_NoReDetection()
    {
        var rawText =
            "전화: [전화번호1], 이메일: [이메일1], 주민번호: [주민등록번호1], 카드: [카드번호1], " +
            "계좌: [계좌번호1], 시크릿: [시크릿1], IP: [IP주소1], MAC: [MAC주소1], 위치: [GPS좌표1]";

        var result = DetectionPipeline.CreateDefault().Detect(rawText);
        Assert.Empty(result.Candidates);
    }

    // ---- 12. URL-embedded PII: value only, via the pipeline ----
    [Fact]
    public void UrlEmbeddedEmail_DetectedThroughPipeline()
    {
        const string email = "synthetic.user@example.com";
        var rawText = $"https://example.com/?email={email}";
        var result = DetectionPipeline.CreateDefault().Detect(rawText);

        Assert.Single(result.Candidates);
        var span = result.Candidates[0].Span;
        Assert.Equal(email, rawText.Substring(span.Start, span.Length));
    }

    // ---- 13. filename-embedded PII: value only, via the pipeline ----
    [Fact]
    public void FilenameEmbeddedPhone_DetectedThroughPipeline()
    {
        const string phone = "01000000000";
        var rawText = $"customer_{phone}.txt";
        var result = DetectionPipeline.CreateDefault().Detect(rawText);

        Assert.Single(result.Candidates);
        var span = result.Candidates[0].Span;
        Assert.Equal(phone, rawText.Substring(span.Start, span.Length));
    }

    // ---- 14. zero-width normalization survives the full pipeline with a correct RawSpan ----
    [Fact]
    public void ZeroWidthCharacter_CorrectRawSpanThroughPipeline()
    {
        var zw = ((char)0x200B).ToString();
        var phone = "010" + zw + "-1234-5678";
        var rawText = $"연락처: {phone} 입니다.";

        var result = DetectionPipeline.CreateDefault().Detect(rawText);
        Assert.Single(result.Candidates);
        var span = result.Candidates[0].Span;
        Assert.Equal(phone, rawText.Substring(span.Start, span.Length));
        Assert.Equal("01012345678", result.Candidates[0].Canonical.Value);
    }

    // ---- 15/16. very long input: no exception, every span stays inside the raw text ----
    [Fact]
    public void VeryLongInput_NoException_SpansStayInBounds()
    {
        var longText = string.Concat(Enumerable.Repeat("일반 텍스트 문장입니다. ", 20000)) + "010-1234-5678";
        var result = DetectionPipeline.CreateDefault().Detect(longText);

        Assert.Single(result.Candidates);
        var span = result.Candidates[0].Span;
        Assert.True(span.Start >= 0);
        Assert.True(span.End <= longText.Length);
    }

    [Fact]
    public void EmptyInput_VeryShortAndVeryLong_NoException()
    {
        Assert.Empty(DetectionPipeline.CreateDefault().Detect("").Candidates);
        var longNoise = string.Concat(Enumerable.Repeat("noise ", 50000));
        var result = DetectionPipeline.CreateDefault().Detect(longNoise);
        foreach (var c in result.Candidates)
        {
            Assert.True(c.Span.End <= longNoise.Length);
        }
    }

    // ---- 17. fuzz/property: randomized detector order -> no exception, semantically stable
    // result for the same input ----
    [Fact]
    public void RandomizedDetectorOrder_NoException_SemanticallyStableResult()
    {
        var rawText =
            "연락처: 010-1234-5678, 이메일: user@example.com, 주민번호: 900101-1234568, " +
            "IP: 203.0.113.42, MAC: 02:00:00:00:00:01, lat=37.5665, lon=126.9780";

        var baseline = DetectionPipeline.CreateDefault().Detect(rawText)
            .Candidates.Select(c => (c.PiiType, c.Span)).OrderBy(t => t.Span.Start).ToList();

        var random = new Random(20260815);
        var detectors = new List<IDetector>
        {
            new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(),
            new CardNumberDetector(), new BankAccountNumberDetector(), new SecretDetector(),
            new IpAddressDetector(), new MacAddressDetector(), new GpsCoordinateDetector(),
        };

        for (int iteration = 0; iteration < 20; iteration++)
        {
            var shuffled = detectors.OrderBy(_ => random.Next()).ToList();
            var result = new DetectionPipeline(shuffled).Detect(rawText);
            var actual = result.Candidates.Select(c => (c.PiiType, c.Span)).OrderBy(t => t.Span.Start).ToList();
            Assert.Equal(baseline, actual);
        }
    }

    // DETECTOR_FAILURE_POLICY: an exception from any single detector propagates out of
    // Detect() unmodified -- it is never caught, swallowed, or turned into a partial result.
    [Fact]
    public void ThrowingDetector_ExceptionPropagates_NotSwallowed()
    {
        var pipeline = new DetectionPipeline([new PhoneDetector(), new ThrowingDetector()]);
        Assert.Throws<InvalidOperationException>(() => pipeline.Detect("연락처: 010-1234-5678 입니다."));
    }

    // ---- 18. synthetic fixture only -- every value used above is a synthetic fixture already
    // used elsewhere in this test suite, never a real or well-known copied identifier. ----
}
