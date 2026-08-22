using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// All card numbers in this file are SYNTHETIC fixtures computed at test time by a local Luhn
// helper -- never a copied real or real-looking issued card number.
public class CardNumberDetectorTests
{
    // Synthetic, clearly non-issuer prefix (not a real network BIN pattern).
    private const string SyntheticPrefix15 = "123456789012345";

    private static readonly string ValidCard16 = BuildLuhnValidCard(SyntheticPrefix15);
    private static readonly string InvalidCard16 = MakeLuhnInvalid(ValidCard16);

    private static string BuildLuhnValidCard(string first15Digits)
    {
        int sum = 0;
        bool doubleDigit = true; // rightmost digit of the 15-digit prefix sits one position
                                  // left of the (unknown) 16th digit, so it is the doubled one.
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

    private static string MakeLuhnInvalid(string validSixteenDigitCard)
    {
        var chars = validSixteenDigitCard.ToCharArray();
        int lastDigit = chars[^1] - '0';
        chars[^1] = (char)('0' + (lastDigit + 1) % 10);
        return new string(chars);
    }

    private static string Hyphenated(string d16) => $"{d16[..4]}-{d16[4..8]}-{d16[8..12]}-{d16[12..]}";
    private static string Spaced(string d16) => $"{d16[..4]} {d16[4..8]} {d16[8..12]} {d16[12..]}";
    private static string MaskedMiddle(string d16) => $"{d16[..4]}-****-****-{d16[12..]}";

    private static IReadOnlyList<DetectionCandidate> Detect(string rawText)
    {
        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);
        return new CardNumberDetector().Detect(context);
    }

    // ---- 1. Luhn-match synthetic card ----
    [Fact]
    public void DetectsLuhnValidCard()
    {
        var results = Detect($"번호: {Hyphenated(ValidCard16)} 확인.");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
    }

    // ---- 2. hyphen format ----
    [Fact]
    public void DetectsHyphenFormat()
    {
        var results = Detect($"카드번호 {Hyphenated(ValidCard16)} 입니다.");
        Assert.Single(results);
    }

    // ---- 3. space format ----
    [Fact]
    public void DetectsSpaceFormat()
    {
        var results = Detect($"카드번호 {Spaced(ValidCard16)} 입니다.");
        Assert.Single(results);
    }

    // ---- 4. continuous digits, no separator ----
    [Fact]
    public void DetectsContinuousDigitsFormat()
    {
        var results = Detect($"카드번호 {ValidCard16} 입니다.");
        Assert.Single(results);
    }

    // ---- 5. mixed separators ----
    [Fact]
    public void DetectsMixedSeparators()
    {
        var mixed = $"{ValidCard16[..4]}-{ValidCard16[4..8]} {ValidCard16[8..12]}-{ValidCard16[12..]}";
        var results = Detect($"카드번호 {mixed} 입니다.");
        Assert.Single(results);
    }

    // ---- 6. zero-width character inside number ----
    [Fact]
    public void ZeroWidthCharactersInsideNumber_AreDetected_WithCorrectRawSpan()
    {
        var zeroWidthSpace = ((char)0x200B).ToString();
        var card = ValidCard16[..4] + zeroWidthSpace + "-" + ValidCard16[4..];
        var rawText = $"카드번호: {card} 확인";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(card, rawText.Substring(span.Start, span.Length));
    }

    // ---- 7. fullwidth digits ----
    [Fact]
    public void FullwidthDigits_AreDetected_WithCorrectRawSpan()
    {
        var fullwidthCard = new string(Hyphenated(ValidCard16).Select(c =>
            c is >= '0' and <= '9' ? (char)(c - '0' + 0xFF10) : c).ToArray());
        var rawText = $"카드번호: {fullwidthCard} 확인";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(fullwidthCard, rawText.Substring(span.Start, span.Length));
        Assert.Equal(ValidCard16, results[0].Canonical.Value);
    }

    // ---- 8. exact RawSpan restoration ----
    [Fact]
    public void RawSpan_MatchesExactOriginalSubstring()
    {
        var card = Hyphenated(ValidCard16);
        var rawText = $"결제카드 번호는 {card} 입니다.";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(card, rawText.Substring(span.Start, span.Length));
    }

    // ---- 9. formatting variants -> same canonical value ----
    [Fact]
    public void DifferentFormatting_SameCanonicalValue()
    {
        var hyphen = Detect($"카드번호 {Hyphenated(ValidCard16)}")[0].Canonical;
        var space = Detect($"카드번호 {Spaced(ValidCard16)}")[0].Canonical;
        var none = Detect($"카드번호 {ValidCard16}")[0].Canonical;

        Assert.Equal(hyphen, space);
        Assert.Equal(space, none);
        Assert.Equal(ValidCard16, hyphen.Value);
    }

    // ---- 10. Luhn match -> High confidence ----
    [Fact]
    public void LuhnValid_YieldsHighConfidence()
    {
        var results = Detect($"번호: {Hyphenated(ValidCard16)} 확인.");
        Assert.Single(results);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
    }

    // ---- 11. Luhn mismatch alone (no card context) -> conservatively not detected ----
    [Fact]
    public void LuhnMismatch_NoContext_NotDetected()
    {
        var results = Detect($"번호: {Hyphenated(InvalidCard16)} 확인.");
        Assert.Empty(results);
    }

    // ---- 12. Luhn mismatch + explicit card context -> kept as Level3, not High ----
    [Fact]
    public void LuhnMismatch_WithExplicitCardContext_StillDetected_NotHighConfidence()
    {
        var results = Detect($"카드번호: {Hyphenated(InvalidCard16)} 확인.");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
        Assert.NotEqual(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 13. partial masking + card context -> detected, High confidence ----
    [Fact]
    public void MaskedNumber_WithExplicitCardContext_Detected_HighConfidence()
    {
        var masked = MaskedMiddle(ValidCard16);
        var results = Detect($"카드번호 {masked} 확인 부탁드립니다.");

        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
        Assert.Equal(masked.Replace("-", ""), results[0].Canonical.Value);
    }

    // ---- 14. generic masked strings not auto-detected ----
    [Fact]
    public void MaskedNumber_WithoutContext_NotDetected()
    {
        var masked = MaskedMiddle(ValidCard16);
        var results = Detect($"번호 {masked} 확인.");
        Assert.Empty(results);
    }

    [Fact]
    public void FullyMaskedString_NeverDetected_EvenWithContext()
    {
        var results = Detect("카드번호 ****-****-****-**** 확인.");
        Assert.Empty(results);
    }

    // ---- 15. partial match inside a longer noise digit run must be rejected ----
    [Fact]
    public void DoesNotDetect_PartialMatchInsideLongerDigitRun()
    {
        var noisy = "9999" + ValidCard16 + "9999";
        var results = Detect($"데이터: {noisy} 확인.");
        Assert.Empty(results);
    }

    // ---- 16. RRN false-positive guard ----
    [Fact]
    public void DoesNotDetect_ResidentRegistrationNumber()
    {
        var results = Detect("주민번호: 900101-1234568 입니다.");
        Assert.Empty(results);
    }

    // ---- 17. phone number false-positive guard ----
    [Fact]
    public void DoesNotDetect_PhoneNumber()
    {
        var results = Detect("연락처: 010-1234-5678 입니다.");
        Assert.Empty(results);
    }

    // ---- 18. order number false-positive guard (no Luhn match, no card context) ----
    [Fact]
    public void DoesNotDetect_OrderNumber()
    {
        var results = Detect($"주문번호 {Hyphenated(InvalidCard16)} 확인 부탁드립니다.");
        Assert.Empty(results);
    }

    // ---- 19. EAN/ISBN false-positive guard ----
    [Fact]
    public void DoesNotDetect_IsbnLikeNumber()
    {
        var results = Detect("ISBN 9788912345678 참고.");
        Assert.Empty(results);
    }

    // ---- 20. timestamp / date / version / IP false-positive guards ----
    [Fact]
    public void DoesNotDetect_Timestamp()
    {
        var results = Detect("타임스탬프: 1691999999999 기록됨.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_Date()
    {
        var results = Detect("회의 일정은 2024-08-15 입니다.");
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
    public void DoesNotDetect_PartialUuid()
    {
        var results = Detect("추적 ID: 550e8400-e29b-41d4-a716-446655440000 확인.");
        Assert.Empty(results);
    }

    // ---- 21. already-protected token not re-detected ----
    [Fact]
    public void DoesNotReDetect_AlreadyAliasedToken()
    {
        var results = Detect("결제 정보는 [카드번호1] 로 이미 보호되어 있습니다.");
        Assert.Empty(results);
    }

    // ---- 22. independent detection alongside Phone, Email, RRN, no overlap ----
    [Fact]
    public void CoexistsWithOtherDetectors_NoOverlap()
    {
        var rawText =
            $"연락처: 010-1234-5678, 이메일: user@example.com, " +
            $"주민번호: 900101-1234568, 카드번호: {Hyphenated(ValidCard16)}";
        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);

        var phoneResults = new PhoneDetector().Detect(context);
        var emailResults = new EmailDetector().Detect(context);
        var rrnResults = new ResidentRegistrationNumberDetector().Detect(context);
        var cardResults = new CardNumberDetector().Detect(context);

        Assert.Single(phoneResults);
        Assert.Single(emailResults);
        Assert.Single(rrnResults);
        Assert.Single(cardResults);

        var allSpans = new[] { phoneResults[0].Span, emailResults[0].Span, rrnResults[0].Span, cardResults[0].Span };
        for (int i = 0; i < allSpans.Length; i++)
        {
            for (int j = i + 1; j < allSpans.Length; j++)
            {
                Assert.False(allSpans[i].OverlapsWith(allSpans[j]));
            }
        }
    }

    // ---- 23. very long input never throws ----
    [Fact]
    public void VeryLongInput_NoException()
    {
        var longText = string.Concat(Enumerable.Repeat("일반 텍스트 문장입니다. ", 20000)) + Hyphenated(ValidCard16);
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
