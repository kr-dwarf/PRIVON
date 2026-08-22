using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// All RRN values in this file are SYNTHETIC fixtures -- constructed to match or deliberately
// mismatch the pre-2020 legacy weighted-sum pattern, never a real person's resident
// registration number. Matching/mismatching that legacy pattern is NOT a claim about whether
// the number is a real, currently-valid RRN (Korea randomized the back 6 digits for
// numbers issued/reissued from Oct 2020 onward) -- see ResidentRegistrationNumberDetector's
// class doc. These names/comments deliberately avoid saying "valid"/"invalid number".
public class ResidentRegistrationNumberDetectorTests
{
    // SYNTHETIC fixture whose 13 digits happen to match the pre-2020 legacy weighted-sum
    // pattern (check digit computed for this file; not a real RRN).
    private const string LegacyChecksumMatchRrn = "900101-1234568";
    // SYNTHETIC: same date/gender as above, deliberately mismatching the legacy pattern
    // (last digit 9 instead of the pattern-matching 8).
    private const string LegacyChecksumMismatchRrn = "900101-1234569";

    private static IReadOnlyList<DetectionCandidate> Detect(string rawText)
    {
        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);
        return new ResidentRegistrationNumberDetector().Detect(context);
    }

    // ---- 1. standard full form ----
    [Fact]
    public void DetectsStandardFullForm()
    {
        var results = Detect($"주민번호: {LegacyChecksumMatchRrn} 입니다.");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
    }

    // ---- 2. no hyphen ----
    [Fact]
    public void DetectsWithoutHyphen()
    {
        var results = Detect("주민번호: 9001011234568 입니다.");
        Assert.Single(results);
    }

    // ---- 3. mixed spacing separators ----
    [Theory]
    [InlineData("900101 1234568")]
    [InlineData("900101 - 1234568")]
    public void DetectsWithSpacedSeparators(string rrn)
    {
        var results = Detect($"주민번호: {rrn} 입니다.");
        Assert.Single(results);
    }

    // ---- 4. zero-width character inside number ----
    [Fact]
    public void ZeroWidthCharactersInsideNumber_AreDetected_WithCorrectRawSpan()
    {
        var zeroWidthSpace = ((char)0x200B).ToString();
        var rrn = "900101" + zeroWidthSpace + "-1234568";
        var rawText = $"주민번호: {rrn} 확인";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(rrn, rawText.Substring(span.Start, span.Length));
    }

    // ---- 5. fullwidth digits ----
    [Fact]
    public void FullwidthDigits_AreDetected_WithCorrectRawSpan()
    {
        const string fullwidthRrn = "９００１０１-１２３４５６８";
        var rawText = $"주민번호: {fullwidthRrn} 확인";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(fullwidthRrn, rawText.Substring(span.Start, span.Length));
        Assert.Equal("9001011234568", results[0].Canonical.Value);
    }

    // ---- 6. exact RawSpan restoration ----
    [Fact]
    public void RawSpan_MatchesExactOriginalSubstring()
    {
        var rawText = $"안녕하세요, 제 번호는 {LegacyChecksumMatchRrn} 입니다.";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(LegacyChecksumMatchRrn, rawText.Substring(span.Start, span.Length));
    }

    // ---- 7. formatting variants -> same canonical value ----
    [Fact]
    public void DifferentFormatting_SameCanonicalValue()
    {
        var hyphen = Detect("900101-1234568")[0].Canonical;
        var noHyphen = Detect("9001011234568")[0].Canonical;
        var spaced = Detect("900101 1234568")[0].Canonical;

        Assert.Equal(hyphen, noHyphen);
        Assert.Equal(noHyphen, spaced);
        Assert.Equal("9001011234568", hyphen.Value);
    }

    // ---- 8. legacy-pattern-matching candidate -> High confidence (isolated: no context keyword) ----
    [Fact]
    public void LegacyChecksumMatch_YieldsHighConfidence()
    {
        var results = Detect($"번호: {LegacyChecksumMatchRrn} 확인.");
        Assert.Single(results);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
    }

    // ---- 9. legacy-pattern mismatch, structurally valid -> still a Level3 candidate, never
    // auto-dropped. This is deliberately NOT phrased as "invalid number": a modern-format
    // (post-Oct-2020) RRN is expected to mismatch the legacy pattern while still being real. ----
    [Fact]
    public void LegacyChecksumMismatch_StillDetected_AtLevel3_NeverAutoDropped()
    {
        var results = Detect($"번호: {LegacyChecksumMismatchRrn} 확인.");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
        Assert.NotEqual(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- explicit context keyword raises confidence to High even without a legacy-pattern
    // match -- this is the modern-format-friendly path: PRIVON should not rely on the legacy
    // checksum to reach High confidence for numbers that legitimately postdate it. ----
    [Theory]
    [InlineData("주민번호")]
    [InlineData("주민등록번호")]
    public void ExplicitRrnContextKeyword_RaisesConfidenceToHigh_EvenWithoutLegacyPatternMatch(string keyword)
    {
        var results = Detect($"{keyword}: {LegacyChecksumMismatchRrn} 확인.");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 10. partial masking (isolated: no context keyword, so floor is Medium not High) ----
    [Theory]
    [InlineData("900101-1******")]
    [InlineData("900101-2******")]
    [InlineData("900101-3******")]
    [InlineData("900101-4******")]
    public void DetectsPartialMasking(string masked)
    {
        var results = Detect($"번호 {masked} 확인.");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
        Assert.NotEqual(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 11. masking with an explicit RRN context keyword -> also reaches High confidence,
    // since checksum can never be computed for a masked number and must not be the only path. ----
    [Fact]
    public void DetectsMaskedNumber_WithExplicitContextKeyword_RaisesToHighConfidence()
    {
        var results = Detect("주민번호 900101-1****** 확인 부탁드립니다.");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 12. plain date false-positive guard ----
    [Fact]
    public void DoesNotDetect_PlainDateAlone()
    {
        var results = Detect("생년월일: 900101 입니다.");
        Assert.Empty(results);
    }

    // ---- 13. phone number false-positive guard ----
    [Fact]
    public void DoesNotDetect_PhoneNumber()
    {
        var results = Detect("연락처: 010-1234-5678 입니다.");
        Assert.Empty(results);
    }

    // ---- 14. order/document number false-positive guards ----
    [Fact]
    public void DoesNotDetect_OrderNumber_InvalidMonth()
    {
        var results = Detect("주문번호 20240815-1234567 확인 부탁드립니다.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_DocumentNumber_InvalidGenderDigit()
    {
        var results = Detect("참조번호: 880101-9012345 확인 부탁드립니다.");
        Assert.Empty(results);
    }

    // ---- 15. partial substring inside a longer noise digit run must be rejected ----
    [Fact]
    public void DoesNotDetect_PartialMatchInsideLongerDigitRun()
    {
        var results = Detect("데이터: 9999001011234567999 확인.");
        Assert.Empty(results);
    }

    // ---- 16. generic 13-digit strings are not auto-detected / not High confidence ----
    [Fact]
    public void DoesNotDetect_Generic13DigitString_InvalidMonth()
    {
        var results = Detect("코드: 9999999999999 확인.");
        Assert.Empty(results);
    }

    [Fact]
    public void Generic13DigitLikeString_NeverHighConfidence_WithoutLegacyPatternMatchOrContext()
    {
        // Structurally date/gender-plausible but mismatches the legacy pattern, and no
        // explicit RRN context keyword nearby -- must never reach High confidence purely from
        // looking like 13 digits.
        var results = Detect($"코드: {LegacyChecksumMismatchRrn} 확인.");
        Assert.Single(results);
        Assert.NotEqual(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- ISBN/EAN, amount, timestamp, version, IPv4, card-number false-positive guards ----
    [Fact]
    public void DoesNotDetect_IsbnLikeNumber()
    {
        var results = Detect("ISBN 9788912345678 참고.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_Amount()
    {
        var results = Detect("총 결제금액은 1,234,567,890,123원 입니다.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_Timestamp()
    {
        var results = Detect("타임스탬프: 1691999999999 기록됨.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_VersionString()
    {
        var results = Detect("버전 1.20.240815.1234567 배포.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_IpAddress()
    {
        var results = Detect("서버 주소는 10.20.30.40 입니다.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_CardNumberCandidate()
    {
        var results = Detect("카드번호 5312-7512-3412-3456 확인.");
        Assert.Empty(results);
    }

    // ---- 17. already-protected token not re-detected ----
    [Fact]
    public void DoesNotReDetect_AlreadyAliasedToken()
    {
        var results = Detect("제 정보는 [주민등록번호1] 로 이미 보호되어 있습니다.");
        Assert.Empty(results);
    }

    // ---- 18. independent detection alongside Phone and Email, no overlap ----
    [Fact]
    public void CoexistsWithPhoneAndEmailDetectors_NoOverlap()
    {
        var rawText = $"연락처: 010-1234-5678, 이메일: user@example.com, 주민번호: {LegacyChecksumMatchRrn}";
        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);

        var phoneResults = new PhoneDetector().Detect(context);
        var emailResults = new EmailDetector().Detect(context);
        var rrnResults = new ResidentRegistrationNumberDetector().Detect(context);

        Assert.Single(phoneResults);
        Assert.Single(emailResults);
        Assert.Single(rrnResults);

        Assert.False(phoneResults[0].Span.OverlapsWith(emailResults[0].Span));
        Assert.False(phoneResults[0].Span.OverlapsWith(rrnResults[0].Span));
        Assert.False(emailResults[0].Span.OverlapsWith(rrnResults[0].Span));
    }

    // ---- 19. very long input never throws ----
    [Fact]
    public void VeryLongInput_NoException()
    {
        var longText = string.Concat(Enumerable.Repeat("일반 텍스트 문장입니다. ", 20000)) + LegacyChecksumMatchRrn;
        var results = Detect(longText);
        Assert.Single(results);
    }

    // ---- empty input never throws ----
    [Fact]
    public void EmptyString_NoException()
    {
        var results = Detect("");
        Assert.Empty(results);
    }
}
