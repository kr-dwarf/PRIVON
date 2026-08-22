using Privon.Core;
using Privon.Detection;

namespace Privon.Detection.Tests;

// Phase 2S.2 — CandidatePolicyEvaluator tests. Candidates are built by hand (same style as
// ExceptionTrustedEvaluatorTests) so every (RiskLevel, Confidence, TrustState) combination can
// be pinned exactly. All fixtures are synthetic filler, never real PII.
public class CandidatePolicyEvaluatorTests
{
    private static EvaluatedCandidate Candidate(RiskLevel risk, DetectionConfidence conf, TrustState trust, PiiType type = PiiType.Phone) =>
        new(new DetectionCandidate(type, new RawSpan(0, 5), risk, conf, new CanonicalValue(type, "SYNTHETIC-VALUE"), "Test"), trust);

    // ---- 1-24. the full, explicitly confirmed 3x3x3 policy matrix (RiskLevel x Confidence x
    // TrustState), hardcoded as a locked-in regression table. ----
    [Theory]
    // Level1
    [InlineData(RiskLevel.Level1, DetectionConfidence.High, TrustState.Trusted, CandidateDisposition.Bypass)]
    [InlineData(RiskLevel.Level1, DetectionConfidence.Medium, TrustState.Trusted, CandidateDisposition.Bypass)]
    [InlineData(RiskLevel.Level1, DetectionConfidence.Low, TrustState.Trusted, CandidateDisposition.Bypass)]
    [InlineData(RiskLevel.Level1, DetectionConfidence.High, TrustState.Untrusted, CandidateDisposition.Protect)]
    [InlineData(RiskLevel.Level1, DetectionConfidence.Medium, TrustState.Untrusted, CandidateDisposition.NeedsDecision)]
    [InlineData(RiskLevel.Level1, DetectionConfidence.Low, TrustState.Untrusted, CandidateDisposition.Bypass)]
    [InlineData(RiskLevel.Level1, DetectionConfidence.High, TrustState.Unknown, CandidateDisposition.Protect)]
    [InlineData(RiskLevel.Level1, DetectionConfidence.Medium, TrustState.Unknown, CandidateDisposition.NeedsDecision)]
    [InlineData(RiskLevel.Level1, DetectionConfidence.Low, TrustState.Unknown, CandidateDisposition.Bypass)]
    // Level2
    [InlineData(RiskLevel.Level2, DetectionConfidence.High, TrustState.Trusted, CandidateDisposition.Bypass)]
    [InlineData(RiskLevel.Level2, DetectionConfidence.Medium, TrustState.Trusted, CandidateDisposition.Bypass)]
    [InlineData(RiskLevel.Level2, DetectionConfidence.Low, TrustState.Trusted, CandidateDisposition.Bypass)]
    [InlineData(RiskLevel.Level2, DetectionConfidence.High, TrustState.Untrusted, CandidateDisposition.Protect)]
    [InlineData(RiskLevel.Level2, DetectionConfidence.Medium, TrustState.Untrusted, CandidateDisposition.Protect)]
    [InlineData(RiskLevel.Level2, DetectionConfidence.Low, TrustState.Untrusted, CandidateDisposition.Protect)]
    [InlineData(RiskLevel.Level2, DetectionConfidence.High, TrustState.Unknown, CandidateDisposition.Protect)]
    [InlineData(RiskLevel.Level2, DetectionConfidence.Medium, TrustState.Unknown, CandidateDisposition.Protect)]
    [InlineData(RiskLevel.Level2, DetectionConfidence.Low, TrustState.Unknown, CandidateDisposition.Protect)]
    // Level3 -- always NeedsDecision, regardless of TrustState or Confidence
    [InlineData(RiskLevel.Level3, DetectionConfidence.High, TrustState.Trusted, CandidateDisposition.NeedsDecision)]
    [InlineData(RiskLevel.Level3, DetectionConfidence.Medium, TrustState.Trusted, CandidateDisposition.NeedsDecision)]
    [InlineData(RiskLevel.Level3, DetectionConfidence.Low, TrustState.Trusted, CandidateDisposition.NeedsDecision)]
    [InlineData(RiskLevel.Level3, DetectionConfidence.High, TrustState.Untrusted, CandidateDisposition.NeedsDecision)]
    [InlineData(RiskLevel.Level3, DetectionConfidence.Medium, TrustState.Untrusted, CandidateDisposition.NeedsDecision)]
    [InlineData(RiskLevel.Level3, DetectionConfidence.Low, TrustState.Untrusted, CandidateDisposition.NeedsDecision)]
    [InlineData(RiskLevel.Level3, DetectionConfidence.High, TrustState.Unknown, CandidateDisposition.NeedsDecision)]
    [InlineData(RiskLevel.Level3, DetectionConfidence.Medium, TrustState.Unknown, CandidateDisposition.NeedsDecision)]
    [InlineData(RiskLevel.Level3, DetectionConfidence.Low, TrustState.Unknown, CandidateDisposition.NeedsDecision)]
    public void FullPolicyMatrix_MatchesConfirmedTable(RiskLevel risk, DetectionConfidence conf, TrustState trust, CandidateDisposition expected)
    {
        var decision = CandidatePolicyEvaluator.Decide(Candidate(risk, conf, trust));
        Assert.Equal(expected, decision);
    }

    // ---- 24 (explicit emphasis). Level3 + Trusted must never become Bypass, even though a
    // real evaluator can never itself produce this combination -- defense-in-depth. ----
    [Fact]
    public void Level3Trusted_NeverBypasses()
    {
        var decision = CandidatePolicyEvaluator.Decide(Candidate(RiskLevel.Level3, DetectionConfidence.High, TrustState.Trusted));
        Assert.NotEqual(CandidateDisposition.Bypass, decision);
        Assert.Equal(CandidateDisposition.NeedsDecision, decision);
    }

    // ---- 25. multiple mixed candidates -> each independently dispositioned ----
    [Fact]
    public void MultipleMixedCandidates_IndependentDispositions()
    {
        var a = Candidate(RiskLevel.Level1, DetectionConfidence.Low, TrustState.Untrusted);   // Bypass
        var b = Candidate(RiskLevel.Level2, DetectionConfidence.High, TrustState.Untrusted);  // Protect
        var c = Candidate(RiskLevel.Level3, DetectionConfidence.Medium, TrustState.Untrusted); // NeedsDecision
        var d = Candidate(RiskLevel.Level1, DetectionConfidence.Medium, TrustState.Untrusted); // NeedsDecision

        var results = CandidatePolicyEvaluator.Evaluate([a, b, c, d]);

        Assert.Equal(CandidateDisposition.Bypass, results[0].Disposition);
        Assert.Equal(CandidateDisposition.Protect, results[1].Disposition);
        Assert.Equal(CandidateDisposition.NeedsDecision, results[2].Disposition);
        Assert.Equal(CandidateDisposition.NeedsDecision, results[3].Disposition);
    }

    // ---- 26-29. candidate preservation: count, order, EvaluatedCandidate, DetectionCandidate unchanged ----
    [Fact]
    public void CandidatePreservation_CountOrderAndValuesUnchanged()
    {
        var a = Candidate(RiskLevel.Level2, DetectionConfidence.High, TrustState.Untrusted, PiiType.Phone);
        var b = Candidate(RiskLevel.Level3, DetectionConfidence.Medium, TrustState.Untrusted, PiiType.Secret);
        var c = Candidate(RiskLevel.Level1, DetectionConfidence.Low, TrustState.Trusted, PiiType.GpsCoordinate);

        var results = CandidatePolicyEvaluator.Evaluate([a, b, c]);

        Assert.Equal(3, results.Count); // 26
        Assert.Equal([a, b, c], results.Select(r => r.Candidate)); // 27 order + 28/29 unchanged (full equality)
    }

    // ---- 30. empty input -> empty output ----
    [Fact]
    public void EmptyInput_EmptyOutput()
    {
        Assert.Empty(CandidatePolicyEvaluator.Evaluate([]));
    }

    // ---- 31. null input -> fail-fast ----
    [Fact]
    public void NullCandidateList_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CandidatePolicyEvaluator.Evaluate(null!));
    }

    [Fact]
    public void NullSingleCandidate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CandidatePolicyEvaluator.Decide(null!));
    }

    // ---- 32. invalid RiskLevel -> fail-fast ----
    [Fact]
    public void InvalidRiskLevel_Throws()
    {
        var candidate = Candidate((RiskLevel)999, DetectionConfidence.High, TrustState.Untrusted);
        Assert.Throws<ArgumentOutOfRangeException>(() => CandidatePolicyEvaluator.Decide(candidate));
    }

    // ---- 33. invalid DetectionConfidence -> fail-fast, even under a RiskLevel branch (Level2)
    // that would not otherwise consume Confidence for a valid input. ----
    [Theory]
    [InlineData(RiskLevel.Level1)]
    [InlineData(RiskLevel.Level2)]
    [InlineData(RiskLevel.Level3)]
    public void InvalidDetectionConfidence_ThrowsRegardlessOfRiskLevelBranch(RiskLevel risk)
    {
        var candidate = Candidate(risk, (DetectionConfidence)999, TrustState.Untrusted);
        Assert.Throws<ArgumentOutOfRangeException>(() => CandidatePolicyEvaluator.Decide(candidate));
    }

    // ---- 34. invalid TrustState -> fail-fast ----
    [Fact]
    public void InvalidTrustState_Throws()
    {
        var candidate = Candidate(RiskLevel.Level1, DetectionConfidence.High, (TrustState)999);
        Assert.Throws<ArgumentOutOfRangeException>(() => CandidatePolicyEvaluator.Decide(candidate));
    }

    // ---- 35/36/37. property tests over the full valid enum space, checked against the
    // general RULES (not a re-copy of the hardcoded table above) -- an independent check. ----
    [Fact]
    public void Property_Level3_AlwaysNeedsDecision_NeverProtectOrBypass()
    {
        foreach (var conf in Enum.GetValues<DetectionConfidence>())
        foreach (var trust in Enum.GetValues<TrustState>())
        {
            var decision = CandidatePolicyEvaluator.Decide(Candidate(RiskLevel.Level3, conf, trust));
            Assert.Equal(CandidateDisposition.NeedsDecision, decision);
        }
    }

    [Fact]
    public void Property_Level2_NonTrusted_AlwaysProtect_RegardlessOfConfidence()
    {
        foreach (var conf in Enum.GetValues<DetectionConfidence>())
        foreach (var trust in new[] { TrustState.Untrusted, TrustState.Unknown })
        {
            var decision = CandidatePolicyEvaluator.Decide(Candidate(RiskLevel.Level2, conf, trust));
            Assert.Equal(CandidateDisposition.Protect, decision);
        }
    }

    [Fact]
    public void Property_AnyLevel_Trusted_ExceptLevel3_AlwaysBypass()
    {
        foreach (var risk in new[] { RiskLevel.Level1, RiskLevel.Level2 })
        foreach (var conf in Enum.GetValues<DetectionConfidence>())
        {
            var decision = CandidatePolicyEvaluator.Decide(Candidate(risk, conf, TrustState.Trusted));
            Assert.Equal(CandidateDisposition.Bypass, decision);
        }
    }

    [Fact]
    public void Property_FullValidMatrix_NeverThrows()
    {
        foreach (var risk in Enum.GetValues<RiskLevel>())
        foreach (var conf in Enum.GetValues<DetectionConfidence>())
        foreach (var trust in Enum.GetValues<TrustState>())
        {
            var decision = CandidatePolicyEvaluator.Decide(Candidate(risk, conf, trust));
            Assert.True(Enum.IsDefined(decision));
        }
    }

    // ---- 38. synthetic-only -- every candidate in this file uses a hand-built filler
    // fixture, never real PII. ----
}
