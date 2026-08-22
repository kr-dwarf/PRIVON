using Privon.Core;
using Privon.Detection;

namespace Privon.Detection.Tests;

public class OverlapResolverTests
{
    private static DetectionCandidate Candidate(int start, int length, RiskLevel level, DetectionConfidence confidence, string detector = "Test") =>
        new(PiiType.Phone, new RawSpan(start, length), level, confidence, new CanonicalValue(PiiType.Phone, $"v{start}"), detector);

    [Fact]
    public void ShortLevel3_BeatsLongerLowerRiskOverlap()
    {
        // A long Level2 match and a short, fully-overlapping Level3 match: Level3 must win
        // even though it is shorter -- this is the hard requirement from the plan.
        var longLowRisk = Candidate(0, 20, RiskLevel.Level2, DetectionConfidence.High);
        var shortHighRisk = Candidate(5, 4, RiskLevel.Level3, DetectionConfidence.Low);

        var resolved = OverlapResolver.Resolve(new[] { longLowRisk, shortHighRisk });

        Assert.Single(resolved);
        Assert.Equal(RiskLevel.Level3, resolved[0].RiskLevel);
    }

    [Fact]
    public void SameRiskLevel_HigherConfidenceWins()
    {
        var lowConf = Candidate(0, 10, RiskLevel.Level2, DetectionConfidence.Low);
        var highConf = Candidate(2, 5, RiskLevel.Level2, DetectionConfidence.High);

        var resolved = OverlapResolver.Resolve(new[] { lowConf, highConf });

        Assert.Single(resolved);
        Assert.Equal(DetectionConfidence.High, resolved[0].Confidence);
    }

    [Fact]
    public void SameRiskAndConfidence_LongerSpanWins()
    {
        var shorter = Candidate(0, 5, RiskLevel.Level2, DetectionConfidence.High);
        var longer = Candidate(0, 10, RiskLevel.Level2, DetectionConfidence.High);

        var resolved = OverlapResolver.Resolve(new[] { shorter, longer });

        Assert.Single(resolved);
        Assert.Equal(10, resolved[0].Span.Length);
    }

    [Fact]
    public void NonOverlappingCandidates_BothKept()
    {
        var a = Candidate(0, 5, RiskLevel.Level2, DetectionConfidence.High);
        var b = Candidate(10, 5, RiskLevel.Level2, DetectionConfidence.High);

        var resolved = OverlapResolver.Resolve(new[] { a, b });

        Assert.Equal(2, resolved.Count);
    }

    [Fact]
    public void LowConfidence_IsNotAutomaticallyDropped()
    {
        // Confidence and RiskLevel are independent -- OverlapResolver must not silently
        // discard a Low-confidence candidate just because of its confidence. It only loses
        // if it actually overlaps a higher-priority candidate.
        var onlyCandidate = Candidate(0, 5, RiskLevel.Level1, DetectionConfidence.Low);

        var resolved = OverlapResolver.Resolve(new[] { onlyCandidate });

        Assert.Single(resolved);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(OverlapResolver.Resolve(Array.Empty<DetectionCandidate>()));
    }

    // Phase 2D: RRN and CardNumber are both real detectors now, both always Level3. Confirms
    // the existing precedence rule (confidence, then span length) still works correctly across
    // two DIFFERENT PiiTypes at the same RiskLevel, not just same-type overlaps.
    [Fact]
    public void SameLevel3_DifferentPiiTypes_HigherConfidenceWins()
    {
        var rrnCandidate = new DetectionCandidate(
            PiiType.ResidentRegistrationNumber, new RawSpan(0, 13), RiskLevel.Level3, DetectionConfidence.Medium,
            new CanonicalValue(PiiType.ResidentRegistrationNumber, "9001011234568"), "ResidentRegistrationNumberDetector");
        var cardCandidate = new DetectionCandidate(
            PiiType.CardNumber, new RawSpan(2, 16), RiskLevel.Level3, DetectionConfidence.High,
            new CanonicalValue(PiiType.CardNumber, "1234567890123456"), "CardNumberDetector");

        var resolved = OverlapResolver.Resolve(new[] { rrnCandidate, cardCandidate });

        Assert.Single(resolved);
        Assert.Equal(PiiType.CardNumber, resolved[0].PiiType);
    }

    [Fact]
    public void SameLevel3_SameConfidence_DifferentPiiTypes_LongerSpanWins()
    {
        var rrnCandidate = new DetectionCandidate(
            PiiType.ResidentRegistrationNumber, new RawSpan(0, 13), RiskLevel.Level3, DetectionConfidence.High,
            new CanonicalValue(PiiType.ResidentRegistrationNumber, "9001011234568"), "ResidentRegistrationNumberDetector");
        var cardCandidate = new DetectionCandidate(
            PiiType.CardNumber, new RawSpan(2, 16), RiskLevel.Level3, DetectionConfidence.High,
            new CanonicalValue(PiiType.CardNumber, "1234567890123456"), "CardNumberDetector");

        var resolved = OverlapResolver.Resolve(new[] { rrnCandidate, cardCandidate });

        Assert.Single(resolved);
        Assert.Equal(PiiType.CardNumber, resolved[0].PiiType);
        Assert.Equal(16, resolved[0].Span.Length);
    }
}
