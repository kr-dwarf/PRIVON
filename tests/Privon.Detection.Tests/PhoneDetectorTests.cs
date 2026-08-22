using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// All test values are synthetic placeholders (홍길동-style dummy numbers), never real PII.
public class PhoneDetectorTests
{
    private static IReadOnlyList<DetectionCandidate> Detect(string rawText)
    {
        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);
        return new PhoneDetector().Detect(context);
    }

    // ---- 1. normal formats ----
    [Theory]
    [InlineData("010-1234-5678")]
    [InlineData("010 1234 5678")]
    [InlineData("01012345678")]
    [InlineData("02-1234-5678")]
    [InlineData("031-123-4567")]
    [InlineData("070-1234-5678")]
    [InlineData("050-1234-5678")]
    [InlineData("0502-1234-5678")]
    public void DetectsStandardFormats(string phone)
    {
        var results = Detect($"연락처: {phone} 입니다.");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 2. +82 international format ----
    [Theory]
    [InlineData("+82 10-1234-5678")]
    [InlineData("+82-10-1234-5678")]
    public void DetectsInternationalFormat_AndCanonicalMatchesDomestic(string phone)
    {
        var intlResults = Detect($"Tel: {phone}");
        var domResults = Detect("Tel: 010-1234-5678");

        Assert.Single(intlResults);
        Assert.Single(domResults);
        Assert.Equal(domResults[0].Canonical, intlResults[0].Canonical);
    }

    // ---- 3. area codes ----
    [Theory]
    [InlineData("032-123-4567")]
    [InlineData("033-123-4567")]
    [InlineData("051-123-4567")]
    [InlineData("064-123-4567")]
    public void DetectsRegionalAreaCodes(string phone)
    {
        var results = Detect($"사무실: {phone}");
        Assert.Single(results);
    }

    // ---- 4. mixed separators ----
    [Fact]
    public void DetectsMixedSeparators()
    {
        var results = Detect("연락처 010 1234-5678 로 문의");
        Assert.Single(results);
    }

    // ---- 5. excessive/loose spacing ----
    [Theory]
    [InlineData("0311234 5678")]
    [InlineData("031 123 4567")]
    public void DetectsLooseSpacing(string phone)
    {
        var results = Detect($"번호는 {phone} 입니다");
        Assert.Single(results);
    }

    // ---- 6. RawIndexMap restores exact raw span ----
    [Fact]
    public void RawSpan_MatchesExactOriginalSubstring()
    {
        const string phone = "010-1234-5678";
        var rawText = $"안녕하세요, 제 번호는 {phone} 입니다.";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(phone, rawText.Substring(span.Start, span.Length));
    }

    // ---- 7. formatting variants -> same canonical value ----
    [Fact]
    public void DifferentFormatting_SameCanonicalValue()
    {
        var a = Detect("010-1234-5678")[0].Canonical;
        var b = Detect("010 1234 5678")[0].Canonical;
        var c = Detect("01012345678")[0].Canonical;

        Assert.Equal(a, b);
        Assert.Equal(b, c);
    }

    // ---- 8-11. false-positive guards ----
    [Fact]
    public void DoesNotDetect_OrderNumber()
    {
        var results = Detect("주문번호 A20240815-99887766 확인 부탁드립니다.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_DocumentNumber()
    {
        var results = Detect("문서번호 DOC-2024-0815-001 참고하세요.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_Date()
    {
        var results = Detect("회의 일정은 2024-08-15 입니다.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_Amount()
    {
        var results = Detect("총 결제금액은 1,234,567원 입니다.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_GenericLongDigitString()
    {
        var results = Detect("참조코드 REF9988776655443322 입니다.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_VersionNumber()
    {
        var results = Detect("빌드 버전은 v2024.08.15 입니다.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_IpAddress()
    {
        var results = Detect("서버 주소는 10.20.30.40 입니다.");
        Assert.Empty(results);
    }

    // ---- 12. already-protected token not re-detected ----
    [Fact]
    public void DoesNotReDetect_AlreadyAliasedToken()
    {
        var results = Detect("연락처는 [전화번호1] 로 이미 보호되어 있습니다.");
        Assert.Empty(results);
    }

    // ---- 13. zero-width / fullwidth digit normalization ----
    [Fact]
    public void FullwidthDigits_AreDetected_WithCorrectRawSpan()
    {
        const string fullwidthPhone = "０１０-１２３４-５６７８";
        var rawText = $"번호: {fullwidthPhone} 확인";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(fullwidthPhone, rawText.Substring(span.Start, span.Length));
        Assert.Equal("01012345678", results[0].Canonical.Value);
    }

    [Fact]
    public void ZeroWidthCharactersInsideNumber_AreDetected_WithCorrectRawSpan()
    {
        // U+200B ZERO WIDTH SPACE inserted mid-number as an evasion attempt. Built from the
        // numeric code point rather than an embedded literal so no invisible character sits
        // in the source file itself.
        var zeroWidthSpace = ((char)0x200B).ToString();
        var zeroWidthPhone = "010" + zeroWidthSpace + "-1234-5678";
        var rawText = $"번호: {zeroWidthPhone} 확인";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(zeroWidthPhone, rawText.Substring(span.Start, span.Length));
        Assert.Equal("01012345678", results[0].Canonical.Value);
    }

    // ---- 14. empty / very long input never throws ----
    [Fact]
    public void EmptyString_NoException()
    {
        var results = Detect("");
        Assert.Empty(results);
    }

    [Fact]
    public void VeryLongInput_NoException()
    {
        var longText = string.Concat(Enumerable.Repeat("일반 텍스트 문장입니다. ", 20000)) + "010-1234-5678";
        var results = Detect(longText);
        Assert.Single(results);
    }
}
