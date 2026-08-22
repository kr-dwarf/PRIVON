using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// Phase 2Q.1 — ContextRiskAdjuster tests. Most tests build DetectionCandidate fixtures by hand
// (same style as OverlapResolverTests) so window/marker/boundary behavior can be pinned exactly
// without depending on any real detector's regex shape -- ContextRiskAdjuster itself never
// re-validates the underlying text, it only looks at RawSpan positions and nearby marker
// matches. A few tests go through DetectionPipeline to confirm URL/filename/zero-width/fuzz
// behavior end to end. All fixtures are synthetic filler text, never real PII.
public class ContextRiskAdjusterTests
{
    private static DetectionCandidate Candidate(int start, int length, RiskLevel risk, DetectionConfidence conf, PiiType type = PiiType.Phone) =>
        new(type, new RawSpan(start, length), risk, conf, new CanonicalValue(type, $"v{start}"), "Test");

    private static IReadOnlyList<DetectionCandidate> Adjust(string rawText, params DetectionCandidate[] candidates) =>
        ContextRiskAdjuster.Adjust(NormalizedView.Build(rawText), candidates);

    // ---- 1. no marker anywhere -> Confidence unchanged ----
    [Fact]
    public void NoMarker_HighStaysHigh()
    {
        var candidate = Candidate(10, 5, RiskLevel.Level2, DetectionConfidence.High);
        var result = Adjust("012345678901234567890123456789012345", candidate);

        Assert.Single(result);
        Assert.Equal(DetectionConfidence.High, result[0].Confidence);
        Assert.Equal(RiskLevel.Level2, result[0].RiskLevel);
    }

    // ---- 2. "sample" nearby, Level2 High -> Medium ----
    [Fact]
    public void SampleMarker_Level2High_StepsDownToMedium()
    {
        const string text = "sample 0123456789";
        var candidate = Candidate(7, 10, RiskLevel.Level2, DetectionConfidence.High);
        var result = Adjust(text, candidate);

        Assert.Equal(DetectionConfidence.Medium, result[0].Confidence);
        Assert.Equal(RiskLevel.Level2, result[0].RiskLevel);
    }

    // ---- 3. "test" nearby, Level2 Medium -> Low ----
    [Fact]
    public void TestMarker_Level2Medium_StepsDownToLow()
    {
        const string text = "test 0123456789";
        var candidate = Candidate(5, 10, RiskLevel.Level2, DetectionConfidence.Medium);
        var result = Adjust(text, candidate);

        Assert.Equal(DetectionConfidence.Low, result[0].Confidence);
        Assert.Equal(RiskLevel.Level2, result[0].RiskLevel);
    }

    // ---- 4. Low + sample -> stays Low (floor) ----
    [Fact]
    public void SampleMarker_AlreadyLow_StaysLow()
    {
        const string text = "sample 0123456789";
        var candidate = Candidate(7, 10, RiskLevel.Level2, DetectionConfidence.Low);
        var result = Adjust(text, candidate);

        Assert.Equal(DetectionConfidence.Low, result[0].Confidence);
    }

    // ---- 5. Level1 Medium + 예시 -> Level1 Low ----
    [Fact]
    public void KoreanMarker_Level1Medium_StepsDownToLow_RiskLevelUnchanged()
    {
        const string text = "예시 0123456789";
        var candidate = Candidate(3, 10, RiskLevel.Level1, DetectionConfidence.Medium);
        var result = Adjust(text, candidate);

        Assert.Equal(DetectionConfidence.Low, result[0].Confidence);
        Assert.Equal(RiskLevel.Level1, result[0].RiskLevel);
    }

    // ---- 6. Level3 High + sample -> completely unchanged ----
    [Fact]
    public void Level3High_SampleMarker_CompletelyUnchanged()
    {
        const string text = "sample 0123456789";
        var candidate = Candidate(7, 10, RiskLevel.Level3, DetectionConfidence.High, PiiType.Secret);
        var result = Adjust(text, candidate);

        Assert.Equal(DetectionConfidence.High, result[0].Confidence);
        Assert.Equal(RiskLevel.Level3, result[0].RiskLevel);
        Assert.Equal(candidate, result[0]);
    }

    // ---- 7. Level3 Medium (synthetic) + test -> completely unchanged ----
    [Fact]
    public void Level3Medium_TestMarker_CompletelyUnchanged()
    {
        const string text = "test 0123456789";
        var candidate = Candidate(5, 10, RiskLevel.Level3, DetectionConfidence.Medium, PiiType.CardNumber);
        var result = Adjust(text, candidate);

        Assert.Equal(candidate, result[0]);
    }

    // ---- 8/9/10/11. RiskLevel / PiiType / RawSpan / CanonicalValue never change ----
    [Fact]
    public void OnlyConfidenceField_EverChanges()
    {
        const string text = "sample 0123456789";
        var candidate = Candidate(7, 10, RiskLevel.Level2, DetectionConfidence.High, PiiType.Email);
        var result = Adjust(text, candidate)[0];

        Assert.Equal(candidate.PiiType, result.PiiType);
        Assert.Equal(candidate.Span, result.Span);
        Assert.Equal(candidate.Canonical, result.Canonical);
        Assert.Equal(candidate.RiskLevel, result.RiskLevel);
        Assert.Equal(candidate.DetectorName, result.DetectorName);
        Assert.NotEqual(candidate.Confidence, result.Confidence); // the one field allowed to change
    }

    // ---- 12. candidate count invariant ----
    [Fact]
    public void CandidateCount_NeverChanges()
    {
        const string text = "sample 0123456789 test 9876543210";
        var a = Candidate(7, 10, RiskLevel.Level2, DetectionConfidence.High);
        var b = Candidate(23, 10, RiskLevel.Level3, DetectionConfidence.High, PiiType.Secret);
        var result = Adjust(text, a, b);

        Assert.Equal(2, result.Count);
    }

    // ---- 13. marker before candidate -> applied ----
    [Fact]
    public void MarkerBeforeCandidate_Applied()
    {
        const string text = "sample 0123456789";
        var candidate = Candidate(7, 10, RiskLevel.Level2, DetectionConfidence.High);
        Assert.Equal(DetectionConfidence.Medium, Adjust(text, candidate)[0].Confidence);
    }

    // ---- 14. marker after candidate -> applied ----
    [Fact]
    public void MarkerAfterCandidate_Applied()
    {
        const string text = "0123456789 sample";
        var candidate = Candidate(0, 10, RiskLevel.Level2, DetectionConfidence.High);
        Assert.Equal(DetectionConfidence.Medium, Adjust(text, candidate)[0].Confidence);
    }

    // ---- 15. marker outside the window -> not applied ----
    [Fact]
    public void MarkerOutsideWindow_NotApplied()
    {
        // "sample " (7 chars) + 40 filler chars + 10-char candidate: the marker's end is 47
        // characters before the candidate's start, well past the 24-char window.
        var text = "sample " + new string('0', 40) + new string('9', 10);
        var candidate = Candidate(47, 10, RiskLevel.Level2, DetectionConfidence.High);
        Assert.Equal(DetectionConfidence.High, Adjust(text, candidate)[0].Confidence);
    }

    // ---- 16/17/18. English marker must be a bound word, not a substring ----
    [Theory]
    [InlineData("contest 0123456789")] // "test" inside "contest"
    [InlineData("testing 0123456789")]
    [InlineData("testimonial 0123456789")]
    public void MarkerSubstring_InsideLargerWord_NotAMarker(string prefix)
    {
        var candidate = Candidate(prefix.IndexOf("0123456789", StringComparison.Ordinal), 10, RiskLevel.Level2, DetectionConfidence.High);
        Assert.Equal(DetectionConfidence.High, Adjust(prefix, candidate)[0].Confidence);
    }

    // "contest@example.com"-shaped text specifically, matching the exact scenario named in the
    // Phase 2Q instructions: the embedded "example" IS a legitimate bound word here (it's
    // exactly decision 179's own illustrative case, "test@example.com"), so this only confirms
    // "test" inside "contest" does not itself independently trigger -- not that the whole
    // string produces no marker at all.
    [Fact]
    public void ContestAtExampleDotCom_TestSubstring_StillNotAMarker_ButExampleDomainIs()
    {
        const string text = "contest@example.com 0123456789";
        var candidate = Candidate(text.IndexOf("0123456789", StringComparison.Ordinal), 10, RiskLevel.Level2, DetectionConfidence.High);
        // "example" is a bound word here (preceded by '@', followed by '.') and is in-window,
        // so the step-down still fires -- just because of "example", never because of "test".
        Assert.Equal(DetectionConfidence.Medium, Adjust(text, candidate)[0].Confidence);
    }

    [Fact]
    public void Latest_DoesNotTriggerTestMarker()
    {
        const string text = "latest 0123456789";
        var candidate = Candidate(7, 10, RiskLevel.Level2, DetectionConfidence.High);
        Assert.Equal(DetectionConfidence.High, Adjust(text, candidate)[0].Confidence);
    }

    // ---- 19. English marker case-insensitivity ----
    [Theory]
    [InlineData("SAMPLE")]
    [InlineData("Sample")]
    [InlineData("sample")]
    public void EnglishMarker_CaseInsensitive(string marker)
    {
        var text = $"{marker} 0123456789";
        var candidate = Candidate(marker.Length + 1, 10, RiskLevel.Level2, DetectionConfidence.High);
        Assert.Equal(DetectionConfidence.Medium, Adjust(text, candidate)[0].Confidence);
    }

    // ---- 20. Korean markers ----
    [Theory]
    [InlineData("예시")]
    [InlineData("테스트")]
    [InlineData("샘플")]
    [InlineData("더미")]
    public void KoreanMarkers_AllRecognized(string marker)
    {
        var text = $"{marker} 0123456789";
        var candidate = Candidate(marker.Length + 1, 10, RiskLevel.Level2, DetectionConfidence.High);
        Assert.Equal(DetectionConfidence.Medium, Adjust(text, candidate)[0].Confidence);
    }

    // ---- 21. marker only inside one of several candidates' windows -> only that one adjusted ----
    [Fact]
    public void MultipleCandidates_OnlyOneWithMarkerInWindow_Adjusted()
    {
        // "sample " then a candidate (in-window), then a large gap, then a second candidate
        // far enough away that "sample" is outside its window.
        var text = "sample " + new string('1', 10) + new string('0', 40) + new string('2', 10);
        var near = Candidate(7, 10, RiskLevel.Level2, DetectionConfidence.High);
        var far = Candidate(57, 10, RiskLevel.Level2, DetectionConfidence.High, PiiType.Email);

        var result = Adjust(text, near, far);
        var nearResult = result.Single(c => c.Span.Start == 7);
        var farResult = result.Single(c => c.Span.Start == 57);

        Assert.Equal(DetectionConfidence.Medium, nearResult.Confidence);
        Assert.Equal(DetectionConfidence.High, farResult.Confidence);
    }

    // ---- 22. URL PII: being a URL alone never changes Confidence; a sample marker nearby does ----
    [Fact]
    public void UrlEmbeddedEmail_NoMarker_ConfidenceUnchanged()
    {
        var rawText = "https://privon-fixture.invalid/?email=synthetic.user@privon-fixture.invalid";
        var result = DetectionPipeline.CreateDefault().Detect(rawText);

        Assert.Single(result.Candidates);
        Assert.Equal(DetectionConfidence.High, result.Candidates[0].Confidence); // EmailDetector's own baseline, unchanged
    }

    [Fact]
    public void UrlEmbeddedEmail_WithSampleMarkerNearby_ConfidenceSteppedDown()
    {
        // Minimal query-string form so the email value falls inside the 24-char window of the
        // "sample" marker -- this test is about marker proximity, not URL length/shape (URL
        // structure itself is already covered above).
        var rawText = "sample ?email=synthetic.user@p.invalid";
        var result = DetectionPipeline.CreateDefault().Detect(rawText);

        Assert.Single(result.Candidates);
        Assert.Equal(DetectionConfidence.Medium, result.Candidates[0].Confidence);
    }

    // ---- 23. filename PII: being a filename alone never changes Confidence ----
    [Fact]
    public void FilenameEmbeddedPhone_NoMarker_ConfidenceUnchanged()
    {
        var rawText = "customer_01000000000.txt";
        var result = DetectionPipeline.CreateDefault().Detect(rawText);

        Assert.Single(result.Candidates);
        Assert.Equal(DetectionConfidence.High, result.Candidates[0].Confidence); // PhoneDetector's own baseline, unchanged
    }

    // ---- 24. zero-width normalization: marker + RawSpan stay stable through the pipeline ----
    [Fact]
    public void ZeroWidthCharacter_MarkerAndRawSpanStableThroughPipeline()
    {
        var zw = ((char)0x200B).ToString();
        var phone = "010" + zw + "-1234-5678";
        var rawText = $"sample {phone} 입니다.";

        var result = DetectionPipeline.CreateDefault().Detect(rawText);
        Assert.Single(result.Candidates);
        var span = result.Candidates[0].Span;
        Assert.Equal(phone, rawText.Substring(span.Start, span.Length));
        Assert.Equal(DetectionConfidence.Medium, result.Candidates[0].Confidence); // High -> Medium
    }

    // ---- 25. very long input: no exception ----
    [Fact]
    public void VeryLongInput_NoException()
    {
        var longText = string.Concat(Enumerable.Repeat("일반 문장입니다. ", 20000)) + "sample 010-1234-5678";
        var result = DetectionPipeline.CreateDefault().Detect(longText);
        Assert.Single(result.Candidates);
    }

    // ---- 26. fuzz/property: candidate count / RawSpan / Level3 invariants hold under
    // randomized candidate sets and randomized filler text ----
    [Fact]
    public void RandomizedCandidates_InvariantsHold()
    {
        var random = new Random(20260815);
        var piiTypes = Enum.GetValues<PiiType>();
        var riskLevels = Enum.GetValues<RiskLevel>();
        var confidences = Enum.GetValues<DetectionConfidence>();
        var words = new[] { "sample", "dummy", "test", "example", "예시", "테스트", "샘플", "더미", "일반", "문장", "이다" };

        for (int iteration = 0; iteration < 300; iteration++)
        {
            var textBuilder = new System.Text.StringBuilder();
            var spans = new List<RawSpan>();
            int cursor = 0;
            int candidateCount = random.Next(1, 6);
            for (int i = 0; i < candidateCount; i++)
            {
                var filler = string.Join(' ', Enumerable.Range(0, random.Next(0, 6)).Select(_ => words[random.Next(words.Length)]));
                textBuilder.Append(filler).Append(' ');
                cursor = textBuilder.Length;
                int len = random.Next(1, 12);
                textBuilder.Append(new string('9', len));
                spans.Add(new RawSpan(cursor, len));
                textBuilder.Append(' ');
            }
            var text = textBuilder.ToString();

            var candidates = spans.Select(s => new DetectionCandidate(
                piiTypes[random.Next(piiTypes.Length)],
                s,
                riskLevels[random.Next(riskLevels.Length)],
                confidences[random.Next(confidences.Length)],
                new CanonicalValue(PiiType.Phone, "v"),
                "Fuzz")).ToList();

            var result = ContextRiskAdjuster.Adjust(NormalizedView.Build(text), candidates);

            Assert.Equal(candidates.Count, result.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                Assert.Equal(candidates[i].Span, result[i].Span);
                Assert.Equal(candidates[i].PiiType, result[i].PiiType);
                Assert.Equal(candidates[i].Canonical, result[i].Canonical);
                Assert.Equal(candidates[i].RiskLevel, result[i].RiskLevel);
                if (candidates[i].RiskLevel == RiskLevel.Level3)
                {
                    Assert.Equal(candidates[i], result[i]); // fully untouched
                }
                Assert.True(result[i].Span.End <= text.Length);
            }
        }
    }

    // ---- 27. synthetic fixture only -- every raw text above is generated filler or a
    // clearly-synthetic placeholder; no real PII appears anywhere in this file. ----
}
