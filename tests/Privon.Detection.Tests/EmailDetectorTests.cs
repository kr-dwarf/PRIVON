using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// All test values are synthetic placeholders on reserved example domains (RFC 2606:
// example.com/example.co.kr/example.net), never real PII.
public class EmailDetectorTests
{
    private static IReadOnlyList<DetectionCandidate> Detect(string rawText)
    {
        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);
        return new EmailDetector().Detect(context);
    }

    // ---- 1. basic email ----
    [Fact]
    public void DetectsBasicEmail()
    {
        var results = Detect("연락처: user@example.com 입니다.");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 2. subdomain / co.kr ----
    [Theory]
    [InlineData("user@example.co.kr")]
    [InlineData("user@mail.example.com")]
    public void DetectsMultiLevelDomains(string email)
    {
        var results = Detect($"이메일 주소는 {email} 입니다.");
        Assert.Single(results);
        Assert.Equal(email, results[0].Canonical.Value);
    }

    // ---- 3. plus addressing ----
    [Fact]
    public void DetectsPlusAddressing()
    {
        var results = Detect("문의: user+tag@example.com 로 보내주세요.");
        Assert.Single(results);
    }

    // ---- 4. underscore / dot local-part ----
    [Theory]
    [InlineData("first.last@example.com")]
    [InlineData("user_name@example.co.kr")]
    public void DetectsUnderscoreAndDotLocalPart(string email)
    {
        var results = Detect($"{email} 로 문의주세요.");
        Assert.Single(results);
    }

    // ---- 5. case-insensitive canonicalization ----
    [Fact]
    public void CaseDifference_SameCanonicalValue()
    {
        var upper = Detect("Test.User@Example.COM")[0].Canonical;
        var lower = Detect("test.user@example.com")[0].Canonical;

        Assert.Equal(lower, upper);
        Assert.Equal("test.user@example.com", upper.Value);
    }

    // ---- 6. different dot structure -> different canonical value ----
    [Fact]
    public void DifferentLocalPartDots_DifferentCanonicalValue()
    {
        var withDot = Detect("test.user@example.com")[0].Canonical;
        var withoutDot = Detect("testuser@example.com")[0].Canonical;

        Assert.NotEqual(withDot, withoutDot);
    }

    // ---- 7. exact RawSpan, punctuation excluded ----
    [Theory]
    [InlineData("문의사항은 (user@example.com) 로 연락주세요.", "user@example.com")]
    [InlineData("이메일: user@example.com, 전화: 010-1234-5678", "user@example.com")]
    [InlineData("주소는 user@example.com. 끝입니다.", "user@example.com")]
    public void RawSpan_ExcludesSurroundingPunctuation(string rawText, string expectedEmail)
    {
        var results = Detect(rawText);
        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(expectedEmail, rawText.Substring(span.Start, span.Length));
    }

    // ---- 8. zero-width normalization -> correct RawSpan ----
    [Fact]
    public void ZeroWidthCharactersInsideEmail_AreDetected_WithCorrectRawSpan()
    {
        var zeroWidthSpace = ((char)0x200B).ToString();
        var email = "user" + zeroWidthSpace + "@example.com";
        var rawText = $"이메일: {email} 확인";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(email, rawText.Substring(span.Start, span.Length));
    }

    // ---- 9. (at) / [at] obfuscation ----
    [Theory]
    [InlineData("user(at)example.com")]
    [InlineData("user (at) example.com")]
    [InlineData("user [at] example.com")]
    public void DetectsAtWordObfuscation(string email)
    {
        var results = Detect($"연락처: {email} 입니다.");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.Medium, results[0].Confidence);
    }

    // ---- 10. (dot) obfuscation ----
    [Fact]
    public void DetectsDotWordObfuscation()
    {
        var results = Detect("연락처: user@example(dot)com 입니다.");
        Assert.Single(results);
        Assert.Equal(DetectionConfidence.Medium, results[0].Confidence);
    }

    // ---- 11. spaced obfuscation, both markers ----
    [Fact]
    public void DetectsFullySpacedObfuscation()
    {
        var results = Detect("연락처: user (at) example (dot) com 입니다.");
        Assert.Single(results);
        Assert.Equal(DetectionConfidence.Medium, results[0].Confidence);
    }

    // ---- 12. obfuscated forms canonicalize to the same value as the plain address ----
    [Fact]
    public void ObfuscatedForm_CanonicalizesLikePlainForm()
    {
        var plain = Detect("user@example.com")[0].Canonical;
        var atMarker = Detect("user(at)example.com")[0].Canonical;
        var dotMarker = Detect("user@example(dot)com")[0].Canonical;
        var both = Detect("user (at) example (dot) com")[0].Canonical;

        Assert.Equal(plain, atMarker);
        Assert.Equal(plain, dotMarker);
        Assert.Equal(plain, both);
    }

    // ---- 13. URL false-positive guard ----
    [Fact]
    public void DoesNotDetect_PlainUrl()
    {
        var results = Detect("자세한 내용은 https://example.com/path/page 를 참고하세요.");
        Assert.Empty(results);
    }

    // ---- 14. SNS handle false-positive guard ----
    [Fact]
    public void DoesNotDetect_SnsHandle()
    {
        var results = Detect("@username 을 팔로우 해주세요.");
        Assert.Empty(results);
    }

    // ---- 15. invalid email format false-positive guards ----
    [Fact]
    public void DoesNotDetect_HostWithoutDomainSuffix()
    {
        var results = Detect("관리자 계정은 admin@localhost 입니다.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_ConsecutiveDotsInLocalPart()
    {
        // The trailing ".." sits directly before '@', so unlike a mid-local-part typo there
        // is no valid embedded "tail@domain" address to fall back to -- the whole thing must
        // stay undetected.
        var results = Detect("주소: user.name..@example.com 확인 필요.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_ConsecutiveDotsInDomain()
    {
        var results = Detect("주소: user@example..com 확인 필요.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_BareAtSymbol()
    {
        var results = Detect("이 사람은 @ 를 좋아한다.");
        Assert.Empty(results);
    }

    [Fact]
    public void DoesNotDetect_AnnotationLikeText()
    {
        var results = Detect("[Obsolete] public string Foo() { return null; }");
        Assert.Empty(results);
    }

    // ---- 16. already-protected token not re-detected ----
    [Fact]
    public void DoesNotReDetect_AlreadyAliasedToken()
    {
        var results = Detect("이메일은 [이메일1] 로 이미 보호되어 있습니다.");
        Assert.Empty(results);
    }

    // ---- 17. independent detection alongside PhoneDetector, no overlap ----
    [Fact]
    public void CoexistsWithPhoneDetector_NoOverlap()
    {
        var rawText = "연락처: 010-1234-5678, 이메일: user@example.com";
        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);

        var phoneResults = new PhoneDetector().Detect(context);
        var emailResults = new EmailDetector().Detect(context);

        Assert.Single(phoneResults);
        Assert.Single(emailResults);
        Assert.False(phoneResults[0].Span.OverlapsWith(emailResults[0].Span));
    }

    // ---- 18. very long input never throws ----
    [Fact]
    public void VeryLongInput_NoException()
    {
        var longText = string.Concat(Enumerable.Repeat("일반 텍스트 문장입니다. ", 20000)) + "user@example.com";
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
