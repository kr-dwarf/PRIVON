using System.Reflection;
using Privon.Core;
using Privon.Detection;

namespace Privon.Detection.Tests;

// Phase 3B STEP3.1 -- Detection Sensitive Diagnostic Surface Hardening regression.
// DETECTION_CANONICAL_DIAGNOSTIC_SURFACE: no Detection-owned DTO's ToString() (nor any
// recursive record formatting reachable through one) may ever expose CanonicalValue.Value --
// only structural/policy metadata (PiiType, Span, RiskLevel, Confidence, DetectorName,
// TrustState, Disposition, Count, and the generated AliasToken display text). Mirrors the
// identical Privon.Windows.ClipboardDiagnosticSurfaceTests precedent (Phase 3A.4 STEP3.2).
//
// No production behavior is exercised or changed here -- DetectionPipeline/detectors/
// normalization/overlap resolution/context adjustment/Exception-Trusted evaluation/
// CandidatePolicyEvaluator/AliasAssigner/AliasReplacer are unmodified; the single real-chain
// test at the bottom of this file uses them exactly as-is, purely to prove the hardened
// ToString overrides hold for genuine detector output, not just hand-built fixtures.
public class DetectionSensitiveDiagnosticSurfaceTests
{
    private const string Sentinel = "RAW-CANONICAL-SENTINEL-739184";

    private static CanonicalValue MakeCanonical(string value = Sentinel) => new(PiiType.Phone, value);

    private static DetectionCandidate MakeCandidate(string canonicalValue = Sentinel) =>
        new(PiiType.Phone, new RawSpan(3, 11), RiskLevel.Level2, DetectionConfidence.High,
            new CanonicalValue(PiiType.Phone, canonicalValue), "PhoneDetector");

    private static EvaluatedCandidate MakeEvaluated(string canonicalValue = Sentinel) =>
        new(MakeCandidate(canonicalValue), TrustState.Untrusted);

    private static CandidatePolicyDecision MakeDecision(string canonicalValue = Sentinel) =>
        new(MakeEvaluated(canonicalValue), CandidateDisposition.Protect);

    private static AliasAssignment MakeAssignment(string canonicalValue = Sentinel, AliasToken? alias = null) =>
        new(MakeDecision(canonicalValue), alias ?? new AliasToken(PiiType.Phone, 1, "[전화번호1]"));

    // ==================================================================
    // 1. CanonicalValue
    // ==================================================================

    [Fact]
    public void CanonicalValue_ToString_DoesNotContainSentinel()
    {
        string rendered = MakeCanonical().ToString();

        Assert.DoesNotContain(Sentinel, rendered);
    }

    [Fact]
    public void CanonicalValue_Interpolation_DoesNotContainSentinel()
    {
        var canonical = MakeCanonical();

        string rendered = $"{canonical}";

        Assert.DoesNotContain(Sentinel, rendered);
    }

    [Fact]
    public void CanonicalValue_ToString_ContainsOnlyPiiTypeMetadata()
    {
        string rendered = MakeCanonical().ToString();

        Assert.Contains(nameof(CanonicalValue.PiiType), rendered);
        Assert.Contains(nameof(PiiType.Phone), rendered);
        // "Value =" is the field-assignment pattern this type's own format would use to expose
        // the sensitive field -- "CanonicalValue" (the type name) legitimately contains the
        // substring "Value", so a bare Assert.DoesNotContain("Value", ...) would be a false
        // positive. Same discipline as ClipboardDiagnosticSurfaceTests' "Text =" check.
        Assert.DoesNotContain("Value =", rendered);
    }

    // ==================================================================
    // 2. DetectionCandidate
    // ==================================================================

    [Fact]
    public void DetectionCandidate_ToString_DoesNotContainSentinel()
    {
        string rendered = MakeCandidate().ToString();

        Assert.DoesNotContain(Sentinel, rendered);
    }

    [Fact]
    public void DetectionCandidate_Interpolation_DoesNotContainSentinel()
    {
        var candidate = MakeCandidate();

        string rendered = $"{candidate}";

        Assert.DoesNotContain(Sentinel, rendered);
    }

    [Fact]
    public void DetectionCandidate_ToString_DoesNotReferenceCanonicalAtAll()
    {
        string rendered = MakeCandidate().ToString();

        // The hardened override never touches Canonical, even indirectly -- confirmed
        // structurally rather than only by sentinel absence, so a future sentinel that happens
        // to collide with other rendered text still cannot hide a real leak.
        Assert.DoesNotContain(nameof(DetectionCandidate.Canonical), rendered);
        Assert.DoesNotContain("Value =", rendered);
    }

    [Fact]
    public void DetectionCandidate_ToString_ContainsSafeStructuralMetadata()
    {
        string rendered = MakeCandidate().ToString();

        Assert.Contains(nameof(DetectionCandidate.PiiType), rendered);
        Assert.Contains(nameof(PiiType.Phone), rendered);
        Assert.Contains(nameof(DetectionCandidate.Span), rendered);
        Assert.Contains("3", rendered);  // Span.Start
        Assert.Contains("11", rendered); // Span.Length
        Assert.Contains(nameof(RiskLevel.Level2), rendered);
        Assert.Contains(nameof(DetectionConfidence.High), rendered);
        Assert.Contains("PhoneDetector", rendered);
    }

    // ==================================================================
    // 3. DetectionResult
    // ==================================================================

    [Fact]
    public void DetectionResult_ToString_DoesNotContainSentinel()
    {
        var result = new DetectionResult([MakeCandidate(), MakeCandidate("RAW-CANONICAL-SENTINEL-284719")]);

        string rendered = result.ToString();

        Assert.DoesNotContain(Sentinel, rendered);
        Assert.DoesNotContain("RAW-CANONICAL-SENTINEL-284719", rendered);
    }

    [Fact]
    public void DetectionResult_ToString_IsCountOnly_NotRecursive()
    {
        var result = new DetectionResult([MakeCandidate(), MakeCandidate()]);

        string rendered = result.ToString();

        Assert.Contains("Count", rendered);
        Assert.Contains("2", rendered);
        Assert.DoesNotContain(nameof(DetectionCandidate.DetectorName), rendered);
        Assert.DoesNotContain("PiiType", rendered);
    }

    // ==================================================================
    // 4. EvaluatedCandidate
    // ==================================================================

    [Fact]
    public void EvaluatedCandidate_ToString_DoesNotContainSentinel()
    {
        string rendered = MakeEvaluated().ToString();

        Assert.DoesNotContain(Sentinel, rendered);
    }

    [Fact]
    public void EvaluatedCandidate_ToString_ContainsSafeMetadata()
    {
        string rendered = MakeEvaluated().ToString();

        Assert.Contains(nameof(PiiType.Phone), rendered);
        Assert.Contains(nameof(RiskLevel.Level2), rendered);
        Assert.Contains(nameof(TrustState.Untrusted), rendered);
    }

    // ==================================================================
    // 5. CandidatePolicyDecision
    // ==================================================================

    [Fact]
    public void CandidatePolicyDecision_ToString_DoesNotContainSentinel()
    {
        string rendered = MakeDecision().ToString();

        Assert.DoesNotContain(Sentinel, rendered);
    }

    [Fact]
    public void CandidatePolicyDecision_ToString_ContainsSafeMetadata()
    {
        string rendered = MakeDecision().ToString();

        Assert.Contains(nameof(PiiType.Phone), rendered);
        Assert.Contains(nameof(TrustState.Untrusted), rendered);
        Assert.Contains(nameof(CandidateDisposition.Protect), rendered);
    }

    // ==================================================================
    // 6. AliasAssignment (item 6 -- alias display text is safe, the wrapped candidate is not)
    // ==================================================================

    [Fact]
    public void AliasAssignment_ToString_WithAlias_DoesNotContainSentinel()
    {
        string rendered = MakeAssignment().ToString();

        Assert.DoesNotContain(Sentinel, rendered);
    }

    [Fact]
    public void AliasAssignment_ToString_WithNullAlias_DoesNotContainSentinel()
    {
        var bypassDecision = new CandidatePolicyDecision(MakeEvaluated(), CandidateDisposition.Bypass);
        var assignment = new AliasAssignment(bypassDecision, Alias: null);

        string rendered = assignment.ToString();

        Assert.DoesNotContain(Sentinel, rendered);
        Assert.Contains(nameof(CandidateDisposition.Bypass), rendered);
    }

    [Fact]
    public void AliasAssignment_ToString_ContainsGeneratedAliasDisplayText_NotCanonicalValue()
    {
        string rendered = MakeAssignment().ToString();

        Assert.Contains("전화번호1", rendered); // the generated token text -- not PII, just a label
        Assert.DoesNotContain(Sentinel, rendered);
    }

    [Fact]
    public void AliasAssignment_ToString_ContainsSafeMetadata()
    {
        string rendered = MakeAssignment().ToString();

        Assert.Contains(nameof(PiiType.Phone), rendered);
        Assert.Contains(nameof(TrustState.Untrusted), rendered);
        Assert.Contains(nameof(CandidateDisposition.Protect), rendered);
    }

    // ==================================================================
    // 7. Debugger surface -- no DebuggerDisplay/DebuggerTypeProxy anywhere in the hardened set,
    // plus AliasToken (audited, left unmodified -- its Value is always generated display text,
    // never a copy of CanonicalValue.Value, so it needed no override; confirmed here alongside
    // the six hardened types so the audit trail is complete in one place).
    // ==================================================================

    [Theory]
    [InlineData(typeof(CanonicalValue))]
    [InlineData(typeof(DetectionCandidate))]
    [InlineData(typeof(DetectionResult))]
    [InlineData(typeof(EvaluatedCandidate))]
    [InlineData(typeof(CandidatePolicyDecision))]
    [InlineData(typeof(AliasAssignment))]
    [InlineData(typeof(AliasToken))]
    public void SensitiveDtoGraph_HasNoDebuggerDisplayOrTypeProxyAttributes(Type type)
    {
        var attributes = type.GetCustomAttributes(inherit: false).Select(a => a.GetType().Name);

        Assert.DoesNotContain(attributes, name => name.Contains("DebuggerDisplay") || name.Contains("DebuggerTypeProxy"));
    }

    // ==================================================================
    // 8. Real chain, real detector output -- not a hand-built sentinel. Proves the hardening
    // holds for genuine DetectionPipeline candidates, reusing the same synthetic fixture and
    // real-API wiring pattern already established in LocalProtectionChainIntegrationTests (no
    // fabricated DetectionCandidate list). Synthetic test phone number only -- no real PII.
    // ==================================================================

    [Fact]
    public void RealDetectionChain_ToStringAtEveryStage_NeverContainsRawOrCanonicalPhoneDigits()
    {
        const string rawText = "연락처 010-1234-5678";
        const string rawDigits = "010-1234-5678";
        const string canonicalDigits = "01012345678";

        var detection = DetectionPipeline.CreateDefault().Detect(rawText);
        var evaluated = ExceptionTrustedEvaluator.Evaluate(detection, [], []);
        var decisions = CandidatePolicyEvaluator.Evaluate(evaluated);
        var assignments = AliasAssigner.Assign(decisions, new AliasMap());

        Assert.Single(detection.Candidates);

        string[] rendered =
        [
            detection.ToString(),
            detection.Candidates[0].ToString(),
            detection.Candidates[0].Canonical.ToString(),
            evaluated[0].ToString(),
            decisions[0].ToString(),
            assignments[0].ToString(),
        ];

        foreach (var text in rendered)
        {
            Assert.DoesNotContain(rawDigits, text);
            Assert.DoesNotContain(canonicalDigits, text);
        }

        // Safe metadata still gets through -- the hardening removes only the sensitive value.
        Assert.Contains(nameof(PiiType.Phone), assignments[0].ToString());
        Assert.Contains(nameof(CandidateDisposition.Protect), assignments[0].ToString());
        Assert.Contains("전화번호1", assignments[0].ToString());
    }
}
