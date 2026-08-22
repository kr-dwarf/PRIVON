using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// All coordinates in this file are either well-known public reference points (city centers,
// used only to exercise realistic decimal-degree magnitudes) or clearly synthetic boundary
// values (0.0, ±90, ±180) -- never a value meant to identify a specific real person's location.
public class GpsCoordinateDetectorTests
{
    private static IReadOnlyList<DetectionCandidate> Detect(string rawText)
    {
        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);
        return new GpsCoordinateDetector().Detect(context);
    }

    // ---- 1. lat/lon labeled decimal ----
    [Fact]
    public void DetectsLabeledLatLon()
    {
        var results = Detect("lat=37.5665, lon=126.9780");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
        Assert.Equal("37.5665,126.978", results[0].Canonical.Value);
    }

    // ---- 2. 위도/경도 한글 문맥 ----
    [Fact]
    public void DetectsKoreanLabeledLatLon()
    {
        var results = Detect("위도 37.5665 경도 126.9780 지점입니다");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 3. GPS 문맥 + bare pair ----
    [Fact]
    public void DetectsGpsContextBarePair()
    {
        var results = Detect("GPS 좌표: 37.5665, 126.9780");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 4. N/S/E/W hemisphere-letter form ----
    [Fact]
    public void DetectsHemisphereLetterForm()
    {
        var results = Detect("좌표: 37.5665N 126.9780E 확인");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
        Assert.Equal("37.5665,126.978", results[0].Canonical.Value);
    }

    [Fact]
    public void DetectsHemisphereLetterForm_SouthWest()
    {
        var results = Detect("좌표: 33.8688S 151.2093W 확인");
        Assert.Single(results);
        Assert.Equal("-33.8688,-151.2093", results[0].Canonical.Value);
    }

    // ---- 5/6. latitude boundary ----
    [Theory]
    [InlineData("-90.0")]
    [InlineData("90.0")]
    public void DetectsLatitudeBoundaryValues(string lat)
    {
        var results = Detect($"lat={lat}, lon=0.0");
        Assert.Single(results);
    }

    // ---- 7/8. longitude boundary ----
    [Theory]
    [InlineData("-180.0")]
    [InlineData("180.0")]
    public void DetectsLongitudeBoundaryValues(string lon)
    {
        var results = Detect($"lat=0.0, lon={lon}");
        Assert.Single(results);
    }

    // ---- 9. latitude out-of-range rejected ----
    [Fact]
    public void DoesNotDetect_LatitudeOutOfRange()
    {
        Assert.Empty(Detect("lat=91.0, lon=0.0"));
    }

    // ---- 10. longitude out-of-range rejected ----
    [Fact]
    public void DoesNotDetect_LongitudeOutOfRange()
    {
        Assert.Empty(Detect("lat=0.0, lon=181.0"));
    }

    // ---- 11. exact RawSpan (GPS-context bare pair: label sits outside the span) ----
    [Fact]
    public void RawSpan_ContextBarePair_ExcludesLabel()
    {
        const string pair = "37.5665, 126.9780";
        var rawText = $"위치 좌표: {pair} 확인";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(pair, rawText.Substring(span.Start, span.Length));
    }

    // ---- RawSpan for the labeled form: the *leading* label is excluded, but a label sitting
    // between the two values is unavoidably inside the single contiguous span (documented,
    // locked-in choice -- see GpsCoordinateDetector class doc). ----
    [Fact]
    public void RawSpan_LabeledPair_LeadingLabelExcluded_MiddleLabelIncluded()
    {
        const string rawText = "lat=37.5665, lon=126.9780 근처입니다";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal("37.5665, lon=126.9780", rawText.Substring(span.Start, span.Length));
    }

    // ---- 12. canonical trailing-zero normalization ----
    [Fact]
    public void TrailingZeros_SameCanonicalValue()
    {
        var padded = Detect("lat=37.566500, lon=126.978000")[0].Canonical;
        var bare = Detect("lat=37.5665, lon=126.978")[0].Canonical;
        Assert.Equal(bare, padded);
    }

    // ---- 13. leading '+' normalization ----
    [Fact]
    public void LeadingPlusSign_SameCanonicalValue()
    {
        var signed = Detect("lat=+37.5665, lon=+126.9780")[0].Canonical;
        var unsigned = Detect("lat=37.5665, lon=126.9780")[0].Canonical;
        Assert.Equal(unsigned, signed);
    }

    // ---- 14. precision is never rounded ----
    [Fact]
    public void DifferentPrecision_DoesNotCollapseToSameCanonicalValue()
    {
        var precise = Detect("lat=37.5665001, lon=126.9780")[0].Canonical;
        var rounded = Detect("lat=37.5665, lon=126.9780")[0].Canonical;
        Assert.NotEqual(rounded, precise);
    }

    // ---- 15. zero-width character inside a coordinate value ----
    [Fact]
    public void ZeroWidthCharacterInsideValue_DetectedWithCorrectRawSpan()
    {
        var zw = ((char)0x200B).ToString();
        var latPart = "37.566" + zw + "5";
        var rawText = $"lat={latPart}, lon=126.9780 확인";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        var expected = latPart + ", lon=126.9780";
        Assert.Equal(expected, rawText.Substring(span.Start, span.Length));
        Assert.Equal("37.5665,126.978", results[0].Canonical.Value);
    }

    // ---- 16/17. bare unlabeled pair: kept as Level1 (ambiguous), never dropped, never Level2 ----
    [Fact]
    public void BarePair_BothWithinLatRange_Level1_LowConfidence()
    {
        // Both 12.5 and 45.8 individually fit within [-90, 90] -- lat/lon order is itself
        // ambiguous, not just "is this GPS at all".
        var results = Detect("12.5, 45.8");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level1, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.Low, results[0].Confidence);
    }

    [Fact]
    public void BarePair_SecondValueOutsideLatRange_Level1_MediumConfidence()
    {
        // 126.978 cannot be read as a latitude, so the lat-then-lon order is structurally
        // forced even without a label -- still Level1 (no strong context keyword), but less
        // ambiguous than the "both fit within ±90" case.
        var results = Detect("37.5665, 126.9780 값을 저장했습니다.");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level1, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.Medium, results[0].Confidence);
    }

    // ---- 18. reversed label order still detected, canonical stays lat-first ----
    [Fact]
    public void ReversedLabelOrder_StillDetected_CanonicalStaysLatFirst()
    {
        var results = Detect("lon=126.9780 lat=37.5665");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
        Assert.Equal("37.5665,126.978", results[0].Canonical.Value);
    }

    // ---- 19. general statistic/amount pairs are not GPS candidates ----
    [Fact]
    public void DoesNotDetect_MoneyOrStatisticPair()
    {
        Assert.Empty(Detect("매출액 1200.5, 비용 850.3"));
    }

    [Fact]
    public void DoesNotDetect_ScreenResolutionPair()
    {
        Assert.Empty(Detect("화면 해상도: 1920, 1080"));
    }

    // ---- 20. version/date/time are not GPS candidates ----
    [Fact]
    public void DoesNotDetect_VersionNumber()
    {
        Assert.Empty(Detect("버전 2.14.3입니다"));
    }

    [Fact]
    public void DoesNotDetect_Date()
    {
        Assert.Empty(Detect("날짜: 2024-01-15 확인"));
    }

    [Fact]
    public void DoesNotDetect_Time()
    {
        Assert.Empty(Detect("시간: 14:30:00 입니다"));
    }

    // ---- 21. IP false-positive avoidance ----
    [Fact]
    public void DoesNotDetect_IpAddress()
    {
        Assert.Empty(Detect("IP: 203.0.113.42 입니다."));
    }

    // ---- 22. MAC false-positive avoidance ----
    [Fact]
    public void DoesNotDetect_MacAddress()
    {
        Assert.Empty(Detect("MAC: 02:00:00:00:00:01 확인"));
    }

    // ---- 23. mixed with other PII, independent detection ----
    [Fact]
    public void CoexistsWithOtherDetectors_NoOverlap()
    {
        var rawText = "연락처: 010-1234-5678, 이메일: user@example.com, lat=37.5665, lon=126.9780";

        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);

        var phoneResults = new PhoneDetector().Detect(context);
        var emailResults = new EmailDetector().Detect(context);
        var gpsResults = new GpsCoordinateDetector().Detect(context);

        Assert.Single(phoneResults);
        Assert.Single(emailResults);
        Assert.Single(gpsResults);

        var allSpans = new[] { phoneResults[0].Span, emailResults[0].Span, gpsResults[0].Span };
        for (int i = 0; i < allSpans.Length; i++)
        {
            for (int j = i + 1; j < allSpans.Length; j++)
            {
                Assert.False(allSpans[i].OverlapsWith(allSpans[j]));
            }
        }
    }

    // ---- 24. already-protected alias token not re-detected ----
    [Fact]
    public void DoesNotReDetect_AlreadyAliasedToken()
    {
        Assert.Empty(Detect("위치는 [GPS좌표1] 로 이미 보호되어 있습니다."));
    }

    // ---- boundary hardening: glued into a larger alphanumeric token ----
    [Fact]
    public void DoesNotDetect_PairEmbeddedInLongerToken()
    {
        Assert.Empty(Detect("데이터: abc37.5665,126.9780xyz 확인"));
    }
}
