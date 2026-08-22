using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// All account numbers in this file are SYNTHETIC fixtures -- never a real bank account.
public class BankAccountNumberDetectorTests
{
    private static IReadOnlyList<DetectionCandidate> Detect(string rawText)
    {
        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);
        return new BankAccountNumberDetector().Detect(context);
    }

    // ---- 1. explicit context + continuous digits ----
    [Fact]
    public void DetectsContinuousDigits_WithAccountContext()
    {
        var results = Detect("계좌번호: 123456789012 입니다.");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 2. explicit context + hyphen groups ----
    [Fact]
    public void DetectsHyphenGroups_WithAccountContext()
    {
        var results = Detect("계좌번호: 123-456-789012 입니다.");
        Assert.Single(results);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 3. "입금 계좌" context ----
    [Fact]
    public void DetectsWithDepositAccountContext()
    {
        var results = Detect("입금 계좌 123-456-789012 로 보내주세요.");
        Assert.Single(results);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 4. "송금 계좌" context ----
    [Fact]
    public void DetectsWithTransferAccountContext()
    {
        var results = Detect("송금 계좌 123-456-789012 확인 부탁드립니다.");
        Assert.Single(results);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 5. bank-name context alone -> Medium (weaker signal than an explicit account word) ----
    [Fact]
    public void DetectsWithBankContext_MediumConfidence()
    {
        var results = Detect("OO은행 123-456-789012 로 입금해주세요.");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.Medium, results[0].Confidence);
    }

    // ---- 6. "예금주" adjacent context ----
    [Fact]
    public void DetectsWithDepositorNameContext()
    {
        var results = Detect("예금주 홍길동, 번호 123-456-789012 입니다.");
        Assert.Single(results);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 7. space separator ----
    [Fact]
    public void DetectsSpaceSeparatedGroups()
    {
        var results = Detect("계좌번호 123 456 789012 입니다.");
        Assert.Single(results);
    }

    // ---- 8. mixed separators ----
    [Fact]
    public void DetectsMixedSeparators()
    {
        var results = Detect("계좌번호 123-456 789012 입니다.");
        Assert.Single(results);
    }

    // ---- 9. zero-width character inside number ----
    [Fact]
    public void ZeroWidthCharactersInsideNumber_AreDetected_WithCorrectRawSpan()
    {
        var zeroWidthSpace = ((char)0x200B).ToString();
        var account = "123-456" + zeroWidthSpace + "-789012";
        var rawText = $"계좌번호: {account} 확인";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(account, rawText.Substring(span.Start, span.Length));
    }

    // ---- 10. fullwidth digits ----
    [Fact]
    public void FullwidthDigits_AreDetected_WithCorrectRawSpan()
    {
        const string fullwidthAccount = "１２３-４５６-７８９０１２";
        var rawText = $"계좌번호: {fullwidthAccount} 확인";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(fullwidthAccount, rawText.Substring(span.Start, span.Length));
        Assert.Equal("123456789012", results[0].Canonical.Value);
    }

    // ---- 11. exact RawSpan restoration ----
    [Fact]
    public void RawSpan_MatchesExactOriginalSubstring()
    {
        const string account = "123-456-789012";
        var rawText = $"제 계좌번호는 {account} 입니다.";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(account, rawText.Substring(span.Start, span.Length));
    }

    // ---- 12. formatting variants -> same canonical value ----
    [Fact]
    public void DifferentFormatting_SameCanonicalValue()
    {
        var hyphen = Detect("계좌번호 123-456-789012")[0].Canonical;
        var space = Detect("계좌번호 123 456 789012")[0].Canonical;
        var none = Detect("계좌번호 123456789012")[0].Canonical;

        Assert.Equal(hyphen, space);
        Assert.Equal(space, none);
        Assert.Equal("123456789012", hyphen.Value);
    }

    // ---- 13. long digit string without context -> not detected ----
    [Fact]
    public void DoesNotDetect_ContinuousDigits_WithoutContext()
    {
        var results = Detect("참고 숫자입니다 123456789012 이상입니다.");
        Assert.Empty(results);
    }

    // ---- 14. hyphenated digit string without context -> not detected ----
    [Fact]
    public void DoesNotDetect_HyphenGroups_WithoutContext()
    {
        var results = Detect("번호 123-456-789012 확인.");
        Assert.Empty(results);
    }

    // ---- 15. partial masking + account context -> detected ----
    [Fact]
    public void DetectsPartialMasking_WithAccountContext()
    {
        var results = Detect("계좌번호 123-***-789012 입니다.");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
        Assert.Equal("123***789012", results[0].Canonical.Value);
    }

    [Fact]
    public void DetectsPartialMasking_LastGroupMasked_WithAccountContext()
    {
        var results = Detect("계좌번호 123-45-*******");
        Assert.Single(results);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 16. generic masked strings never detected ----
    [Fact]
    public void DoesNotDetect_MaskedNumber_WithoutContext()
    {
        var results = Detect("번호 123-***-789012 확인.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_FullyMaskedGroups_EvenWithContext()
    {
        var results = Detect("계좌번호 ***-***-****** 입니다.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_UnseparatedMaskString()
    {
        var results = Detect("계좌번호 ****** 입니다.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_PartiallyMaskedUnseparatedString()
    {
        var results = Detect("계좌번호 1234**** 입니다.");
        Assert.Empty(results);
    }

    // ---- 17. partial match inside a longer noise digit run must be rejected ----
    [Fact]
    public void DoesNotDetect_PartialMatchInsideLongerDigitRun()
    {
        var results = Detect("계좌번호 관련 데이터: 999912345678901234569999 확인.");
        Assert.Empty(results);
    }

    // ---- 18. phone number false-positive guard (even with account context nearby) ----
    [Fact]
    public void DoesNotDetect_PhoneNumber_EvenWithAccountContextNearby()
    {
        var results = Detect("계좌 문의는 010-1234-5678 로 연락주세요.");
        Assert.Empty(results);
    }

    // ---- 19. RRN false-positive guard. Uses the no-hyphen RRN form deliberately: it is a
    // clean, boundary-safe 13-digit run that ResidentRegistrationNumberDetector also matches,
    // so this actually exercises the cross-detector guard (unlike the hyphenated
    // "900101-1234568" form, which BankAccountNumberDetector's own boundary check already
    // rejects on its own -- a 7-digit remainder would dangle after the group-size cap). ----
    [Fact]
    public void DoesNotDetect_ResidentRegistrationNumber_EvenWithAccountContextNearby()
    {
        var results = Detect("계좌 명의자 주민번호는 9001011234568 입니다.");
        Assert.Empty(results);
    }

    // ---- 20. card number false-positive guard (also naturally out of the 10-14 digit range) ----
    [Fact]
    public void DoesNotDetect_CardNumber_EvenWithAccountContextNearby()
    {
        var results = Detect("계좌번호 대신 카드번호 1234-5678-9012-3456 사용 부탁드립니다.");
        Assert.Empty(results);
    }

    // ---- 21. date / timestamp false-positive guards (no account context) ----
    [Fact]
    public void DoesNotDetect_Date()
    {
        var results = Detect("회의 일정은 2024-08-15 입니다.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_Timestamp()
    {
        var results = Detect("타임스탬프: 1691999999999 기록됨.");
        Assert.Empty(results);
    }

    // ---- 22. order / tracking / document number false-positive guards (no account context) ----
    [Fact]
    public void DoesNotDetect_OrderNumber()
    {
        var results = Detect("주문번호 20240815-1234567 확인 부탁드립니다.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_TrackingNumber()
    {
        var results = Detect("운송장번호: 123456789012 입니다.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_DocumentNumber()
    {
        var results = Detect("문서번호 DOC-2024-08150012 참고하세요.");
        Assert.Empty(results);
    }

    // ---- 23. ISBN/EAN false-positive guard ----
    [Fact]
    public void DoesNotDetect_IsbnLikeNumber()
    {
        var results = Detect("ISBN 9788912345678 참고.");
        Assert.Empty(results);
    }

    // ---- 24. IPv4 false-positive guard ----
    [Fact]
    public void DoesNotDetect_IpAddress()
    {
        var results = Detect("계좌번호 대신 서버 주소는 10.20.30.40 입니다.");
        Assert.Empty(results);
    }

    // ---- 25. already-protected token not re-detected ----
    [Fact]
    public void DoesNotReDetect_AlreadyAliasedToken()
    {
        var results = Detect("제 정보는 [계좌번호1] 로 이미 보호되어 있습니다.");
        Assert.Empty(results);
    }

    // ---- 26. independent detection alongside Phone/Email/RRN/Card, no overlap ----
    [Fact]
    public void CoexistsWithOtherDetectors_NoOverlap()
    {
        var validCard = BuildLuhnValidCard("123456789012345");
        var rawText =
            $"연락처: 010-1234-5678, 이메일: user@example.com, " +
            $"주민번호: 900101-1234568, 카드번호: {validCard[..4]}-{validCard[4..8]}-{validCard[8..12]}-{validCard[12..]}, " +
            $"계좌번호: 123-456-789012";
        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);

        var phoneResults = new PhoneDetector().Detect(context);
        var emailResults = new EmailDetector().Detect(context);
        var rrnResults = new ResidentRegistrationNumberDetector().Detect(context);
        var cardResults = new CardNumberDetector().Detect(context);
        var accountResults = new BankAccountNumberDetector().Detect(context);

        Assert.Single(phoneResults);
        Assert.Single(emailResults);
        Assert.Single(rrnResults);
        Assert.Single(cardResults);
        Assert.Single(accountResults);

        var allSpans = new[]
        {
            phoneResults[0].Span, emailResults[0].Span, rrnResults[0].Span,
            cardResults[0].Span, accountResults[0].Span,
        };
        for (int i = 0; i < allSpans.Length; i++)
        {
            for (int j = i + 1; j < allSpans.Length; j++)
            {
                Assert.False(allSpans[i].OverlapsWith(allSpans[j]));
            }
        }
    }

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

    // ---- 27. very long input never throws ----
    [Fact]
    public void VeryLongInput_NoException()
    {
        var longText = string.Concat(Enumerable.Repeat("일반 텍스트 문장입니다. ", 20000)) + "계좌번호 123-456-789012";
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
