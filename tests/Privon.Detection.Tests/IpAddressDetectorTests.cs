using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// All addresses in this file are RFC 5737/3849/1918/reserved documentation/synthetic ranges
// (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24, 2001:db8::/32, 10/8, 172.16/12, 192.168/16,
// 127.0.0.1, ::1) -- never a real, identifying IP address.
public class IpAddressDetectorTests
{
    private static IReadOnlyList<DetectionCandidate> Detect(string rawText)
    {
        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);
        return new IpAddressDetector().Detect(context);
    }

    // ---- 1. normal IPv4 ----
    [Fact]
    public void DetectsNormalIpv4()
    {
        var results = Detect("서버 주소는 203.0.113.42 입니다.");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
        Assert.Equal("203.0.113.42", results[0].Canonical.Value);
    }

    // ---- 2. 0/255 boundary ----
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    public void DetectsOctetBoundaryValues(string ip)
    {
        var results = Detect($"주소: {ip} 확인");
        Assert.Single(results);
    }

    // ---- 3. octet > 255 rejected ----
    [Theory]
    [InlineData("256.1.1.1")]
    [InlineData("999.999.999.999")]
    public void DoesNotDetect_OctetOutOfRange(string text)
    {
        Assert.Empty(Detect($"주소: {text} 확인"));
    }

    // ---- 4. too few / too many segments ----
    [Fact]
    public void DoesNotDetect_TooFewSegments()
    {
        Assert.Empty(Detect("주소: 1.2.3 확인"));
    }

    [Fact]
    public void DoesNotDetect_TooManySegments()
    {
        Assert.Empty(Detect("주소: 1.2.3.4.5 확인"));
    }

    // ---- 5. separated from a following port ----
    [Fact]
    public void RawSpan_ExcludesPortSuffix()
    {
        const string ip = "203.0.113.42";
        var rawText = $"{ip}:8080";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(ip, rawText.Substring(span.Start, span.Length));
    }

    // ---- 6. IP inside a URL ----
    [Fact]
    public void DetectsIpv4InsideUrl()
    {
        const string ip = "203.0.113.42";
        var rawText = $"http://{ip}/path";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(ip, rawText.Substring(span.Start, span.Length));
    }

    // ---- 7. exact RawSpan restoration ----
    [Fact]
    public void RawSpan_MatchesExactOriginalSubstring()
    {
        const string ip = "203.0.113.42";
        var rawText = $"연결된 IP는 {ip} 입니다.";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(ip, rawText.Substring(span.Start, span.Length));
    }

    // ---- 8. zero-width character inside IPv4 ----
    [Fact]
    public void ZeroWidthCharacterInsideIpv4_DetectedWithCorrectRawSpan()
    {
        var zeroWidthSpace = ((char)0x200B).ToString();
        var ip = "203.0" + zeroWidthSpace + ".113.42";
        var rawText = $"주소: {ip} 확인";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(ip, rawText.Substring(span.Start, span.Length));
        Assert.Equal("203.0.113.42", results[0].Canonical.Value);
    }

    // ---- 9. IPv4 canonicalization round-trips ----
    [Fact]
    public void Ipv4Canonicalization_RoundTrips()
    {
        var results = Detect("192.0.2.1");
        Assert.Single(results);
        Assert.Equal("192.0.2.1", results[0].Canonical.Value);
    }

    // ---- 10. full IPv6 ----
    [Fact]
    public void DetectsFullIpv6()
    {
        var results = Detect("주소: 2001:0db8:0000:0000:0000:ff00:0042:8329 확인");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 11. compressed IPv6 (::) ----
    [Fact]
    public void DetectsCompressedIpv6()
    {
        var results = Detect("주소: 2001:db8::ff00:42:8329 확인");
        Assert.Single(results);
    }

    // ---- 12. loopback ::1 ----
    [Fact]
    public void DetectsIpv6Loopback()
    {
        var results = Detect("주소: ::1 확인");
        Assert.Single(results);
    }

    // ---- 13. leading-zero variants canonicalize identically ----
    [Fact]
    public void Ipv6LeadingZeroVariants_SameCanonicalValue()
    {
        var withLeadingZeros = Detect("2001:0db8::0001")[0].Canonical;
        var compact = Detect("2001:db8::1")[0].Canonical;

        Assert.Equal(compact, withLeadingZeros);
    }

    [Fact]
    public void FullAndCompressedIpv6_SameCanonicalValue()
    {
        var full = Detect("2001:0db8:0000:0000:0000:ff00:0042:8329")[0].Canonical;
        var compressed = Detect("2001:db8::ff00:42:8329")[0].Canonical;

        Assert.Equal(compressed, full);
    }

    // ---- 14. IPv6 URL literal: brackets excluded from span ----
    [Fact]
    public void Ipv6UrlLiteral_RawSpan_ExcludesBrackets()
    {
        const string ip = "2001:db8::1";
        var rawText = $"http://[{ip}]:8080/";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(ip, rawText.Substring(span.Start, span.Length));
    }

    // ---- 15. invalid IPv6 rejected ----
    [Fact]
    public void DoesNotDetect_InvalidHexInIpv6()
    {
        Assert.Empty(Detect("주소: 2001:db8:gggg::1 확인"));
    }

    [Fact]
    public void DoesNotDetect_ClockTime_TooFewGroupsNoCompression()
    {
        Assert.Empty(Detect("시간: 12:30:45 입니다"));
    }

    // ---- 16. IPv4-mapped IPv6 ----
    [Fact]
    public void DetectsIpv4MappedIpv6()
    {
        var results = Detect("주소: ::ffff:203.0.113.42 확인");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
    }

    // ---- 17. private IPv4 still produces a candidate ----
    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    public void DetectsPrivateUseIpv4_AsCandidate(string ip)
    {
        var results = Detect($"내부 주소: {ip} 확인");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
    }

    // ---- 18. loopback still produces a candidate ----
    [Fact]
    public void DetectsIpv4Loopback_AsCandidate()
    {
        var results = Detect("로컬: 127.0.0.1 확인");
        Assert.Single(results);
    }

    // ---- 19. documentation-range addresses still produce candidates ----
    [Theory]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    public void DetectsDocumentationRangeIpv4_AsCandidate(string ip)
    {
        var results = Detect($"예시 주소: {ip} 참고");
        Assert.Single(results);
    }

    // ---- 20. version-like ambiguity: detector never suppresses, policy is a future decision ----
    [Fact]
    public void StructurallyValidIpv4_StillDetected_EvenWhenVersionLikeInContext()
    {
        // "1.2.3.4" is structurally a valid IPv4 address; whether this specific occurrence
        // means "an IP address" or "a version string" is a semantic question this detector
        // deliberately does not attempt to resolve -- see class doc / Phase 2K OPEN_QUESTION.
        var results = Detect("버전 1.2.3.4 로 업데이트 되었습니다.");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
    }

    // ---- 21-24. other PII detectors' values are not mistaken for IP addresses ----
    [Fact]
    public void DoesNotDetect_PhoneNumber()
    {
        Assert.Empty(Detect("연락처: 010-1234-5678 입니다."));
    }

    [Fact]
    public void DoesNotDetect_ResidentRegistrationNumber()
    {
        Assert.Empty(Detect("주민번호: 900101-1234568 입니다."));
    }

    [Fact]
    public void DoesNotDetect_CardNumber()
    {
        Assert.Empty(Detect("카드번호: 1234-5678-9012-3456 입니다."));
    }

    [Fact]
    public void DoesNotDetect_BankAccountNumber()
    {
        Assert.Empty(Detect("계좌번호: 123-456-789012 입니다."));
    }

    [Fact]
    public void DoesNotDetect_PhoneNumber_DotFormatted()
    {
        // Korean phone numbers are not conventionally dot-separated, but confirm a plausible
        // dotted grouping still doesn't structurally collapse into a valid 0-255 IPv4 shape.
        Assert.Empty(Detect("연락처: 010.1234.5678 입니다."));
    }

    // ---- 25. already-protected alias token not re-detected ----
    [Fact]
    public void DoesNotReDetect_AlreadyAliasedToken()
    {
        Assert.Empty(Detect("서버 주소는 [IP주소1] 로 이미 보호되어 있습니다."));
    }

    // ---- 26. mixed PII sentence, independent detection ----
    [Fact]
    public void CoexistsWithOtherDetectors_NoOverlap()
    {
        var rawText =
            "연락처: 010-1234-5678, 이메일: user@example.com, 주민번호: 900101-1234568, " +
            "카드번호: 1234-5678-9012-3456, 계좌번호: 123-456-789012, IP: 203.0.113.42";

        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);

        var phoneResults = new PhoneDetector().Detect(context);
        var emailResults = new EmailDetector().Detect(context);
        var rrnResults = new ResidentRegistrationNumberDetector().Detect(context);
        var cardResults = new CardNumberDetector().Detect(context);
        var accountResults = new BankAccountNumberDetector().Detect(context);
        var ipResults = new IpAddressDetector().Detect(context);

        Assert.Single(phoneResults);
        Assert.Single(emailResults);
        Assert.Single(rrnResults);
        Assert.Single(cardResults);
        Assert.Single(accountResults);
        Assert.Single(ipResults);
        Assert.Equal("203.0.113.42", ipResults[0].Canonical.Value);

        var allSpans = new[]
        {
            phoneResults[0].Span, emailResults[0].Span, rrnResults[0].Span,
            cardResults[0].Span, accountResults[0].Span, ipResults[0].Span,
        };
        for (int i = 0; i < allSpans.Length; i++)
        {
            for (int j = i + 1; j < allSpans.Length; j++)
            {
                Assert.False(allSpans[i].OverlapsWith(allSpans[j]));
            }
        }
    }

    // ---- boundary hardening: partial substring inside a longer token must be rejected ----
    [Fact]
    public void DoesNotDetect_Ipv4EmbeddedInLongerAlphanumericToken()
    {
        Assert.Empty(Detect("데이터: abc192.0.2.1xyz 확인"));
    }

    [Fact]
    public void DoesNotDetect_Ipv4EmbeddedInLongerDigitRun()
    {
        Assert.Empty(Detect("데이터: 999192.0.2.1 확인"));
    }

    // ---- Phase 2O.1: IPv4 trailing-dot boundary hardening ----
    // A trailing '.' not followed by a digit (a filename extension, end of text, other
    // punctuation) can't be starting another octet, so it no longer blanket-rejects the match.
    [Theory]
    [InlineData("192.0.2.1.log")]
    [InlineData("192.0.2.1.txt")]
    [InlineData("192.0.2.1.json")]
    public void DetectsIpv4_FollowedByFilenameExtension(string text)
    {
        var results = Detect(text);
        Assert.Single(results);
        Assert.Equal("192.0.2.1", results[0].Canonical.Value);
        var span = results[0].Span;
        Assert.Equal("192.0.2.1", text.Substring(span.Start, span.Length));
    }

    // A trailing '.' immediately followed by a digit is still treated as a possible additional
    // octet -- the whole candidate is rejected outright rather than cut down to a partial
    // match, same conservative behavior as the existing "too many segments" case.
    [Theory]
    [InlineData("192.0.2.1.5")]
    [InlineData("192.0.2.1.999")]
    public void DoesNotDetect_Ipv4FollowedByTrailingDotDigit(string text)
    {
        Assert.Empty(Detect(text));
    }

    // ---- 27. very long input never throws ----
    [Fact]
    public void VeryLongInput_NoException()
    {
        var longText = string.Concat(Enumerable.Repeat("일반 텍스트 문장입니다. ", 20000)) + "203.0.113.42";
        var results = Detect(longText);
        Assert.Single(results);
    }

    // ---- empty input never throws ----
    [Fact]
    public void EmptyString_NoException()
    {
        Assert.Empty(Detect(""));
    }
}
