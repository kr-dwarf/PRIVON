using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// All MAC addresses in this file are IEEE-reserved-for-documentation-style synthetic values
// (locally-administered/multicast bit patterns with an all-zero payload) -- never a real,
// identifying device MAC address.
public class MacAddressDetectorTests
{
    private static IReadOnlyList<DetectionCandidate> Detect(string rawText)
    {
        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);
        return new MacAddressDetector().Detect(context);
    }

    // ---- 1. colon MAC ----
    [Fact]
    public void DetectsColonFormattedMac()
    {
        var results = Detect("장치 MAC 주소: 02:00:00:00:00:01 확인");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
        Assert.Equal("02:00:00:00:00:01", results[0].Canonical.Value);
    }

    // ---- 2. hyphen MAC ----
    [Fact]
    public void DetectsHyphenFormattedMac()
    {
        var results = Detect("장치 MAC 주소: 02-00-00-00-00-01 확인");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
        Assert.Equal("02:00:00:00:00:01", results[0].Canonical.Value);
    }

    // ---- 3. uppercase/lowercase ----
    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF")]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    [InlineData("Aa:Bb:Cc:Dd:Ee:Ff")]
    public void DetectsMixedCaseHexDigits(string mac)
    {
        var results = Detect($"MAC: {mac} 입니다");
        Assert.Single(results);
        Assert.Equal("aa:bb:cc:dd:ee:ff", results[0].Canonical.Value);
    }

    // ---- 4. canonical 동일성: separator + case both converge ----
    [Fact]
    public void ColonAndHyphenAndCaseVariants_SameCanonicalValue()
    {
        var colonLower = Detect("02:00:00:00:00:01")[0].Canonical;
        var hyphenUpper = Detect("02-00-00-00-00-01")[0].Canonical;
        var colonUpper = Detect("02:00:00:00:00:01".ToUpperInvariant())[0].Canonical;

        Assert.Equal(colonLower, hyphenUpper);
        Assert.Equal(colonLower, colonUpper);
    }

    // ---- 5. 정확한 RawSpan ----
    [Fact]
    public void RawSpan_MatchesExactOriginalSubstring()
    {
        const string mac = "02:00:00:00:00:01";
        var rawText = $"MAC={mac},";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(mac, rawText.Substring(span.Start, span.Length));
    }

    // ---- 6. zero-width character inside a MAC ----
    [Fact]
    public void ZeroWidthCharacterInsideMac_DetectedWithCorrectRawSpan()
    {
        var zeroWidthSpace = ((char)0x200B).ToString();
        var mac = "02:00:00:00:00:0" + zeroWidthSpace + "1";
        var rawText = $"MAC: {mac} 확인";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(mac, rawText.Substring(span.Start, span.Length));
        Assert.Equal("02:00:00:00:00:01", results[0].Canonical.Value);
    }

    // ---- 7. invalid hex rejected ----
    [Fact]
    public void DoesNotDetect_InvalidHexDigit()
    {
        Assert.Empty(Detect("MAC: 02:00:00:00:00:GG 확인"));
    }

    // ---- 8. too few groups ----
    [Fact]
    public void DoesNotDetect_TooFewGroups()
    {
        Assert.Empty(Detect("MAC: 02:00:00:00:00 확인"));
    }

    // ---- 9. too many groups ----
    [Fact]
    public void DoesNotDetect_TooManyGroups()
    {
        Assert.Empty(Detect("MAC: 02:00:00:00:00:00:01 확인"));
    }

    // ---- 10. partial match inside a larger hex token rejected ----
    [Fact]
    public void DoesNotDetect_MacEmbeddedInLongerHexToken()
    {
        Assert.Empty(Detect("데이터: AA02:00:00:00:00:01BB 확인"));
    }

    // ---- 11. locally administered MAC still produces a candidate ----
    [Fact]
    public void DetectsLocallyAdministeredMac_AsCandidate()
    {
        // 0x02 = 0000_0010: U/L bit (bit 1) set -> locally administered, I/G bit (bit 0) clear -> unicast.
        var results = Detect("MAC: 02:00:00:00:00:01 확인");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
    }

    // ---- 12. multicast MAC still produces a candidate ----
    [Fact]
    public void DetectsMulticastMac_AsCandidate()
    {
        // 0x03 = 0000_0011: I/G bit (bit 0) set -> multicast.
        var results = Detect("MAC: 03:00:00:00:00:01 확인");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
    }

    // ---- 13. IPv6 false-positive avoidance ----
    [Fact]
    public void DoesNotDetect_FullIpv6()
    {
        Assert.Empty(Detect("주소: 2001:0db8:0000:0000:0000:ff00:0042:8329 확인"));
    }

    [Fact]
    public void DoesNotDetect_CompressedIpv6()
    {
        Assert.Empty(Detect("주소: 2001:db8::1 확인"));
    }

    // ---- 14. UUID false-positive avoidance ----
    [Fact]
    public void DoesNotDetect_Uuid()
    {
        Assert.Empty(Detect("id: 550e8400-e29b-41d4-a716-446655440000 확인"));
    }

    // ---- 15. hash/hex-string false-positive avoidance ----
    [Fact]
    public void DoesNotDetect_UnseparatedHexHash()
    {
        Assert.Empty(Detect("sha256: e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85 확인"));
    }

    // ---- 16. mixed with other PII, independent detection ----
    [Fact]
    public void CoexistsWithOtherDetectors_NoOverlap()
    {
        var rawText = "연락처: 010-1234-5678, 이메일: user@example.com, MAC: 02:00:00:00:00:01";

        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);

        var phoneResults = new PhoneDetector().Detect(context);
        var emailResults = new EmailDetector().Detect(context);
        var macResults = new MacAddressDetector().Detect(context);

        Assert.Single(phoneResults);
        Assert.Single(emailResults);
        Assert.Single(macResults);
        Assert.Equal("02:00:00:00:00:01", macResults[0].Canonical.Value);

        var allSpans = new[] { phoneResults[0].Span, emailResults[0].Span, macResults[0].Span };
        for (int i = 0; i < allSpans.Length; i++)
        {
            for (int j = i + 1; j < allSpans.Length; j++)
            {
                Assert.False(allSpans[i].OverlapsWith(allSpans[j]));
            }
        }
    }

    // ---- 17. already-protected alias token not re-detected ----
    [Fact]
    public void DoesNotReDetect_AlreadyAliasedToken()
    {
        Assert.Empty(Detect("장치 MAC은 [MAC주소1] 로 이미 보호되어 있습니다."));
    }

    // ---- boundary hardening: too many groups via trailing separator+group ----
    [Fact]
    public void DoesNotDetect_MacFollowedByExtraGroup_HyphenForm()
    {
        Assert.Empty(Detect("MAC: 02-00-00-00-00-00-01 확인"));
    }

    // ---- clock-time-shaped strings never match (too few groups) ----
    [Fact]
    public void DoesNotDetect_ClockTime()
    {
        Assert.Empty(Detect("시간: 12:30:45 입니다"));
    }
}
