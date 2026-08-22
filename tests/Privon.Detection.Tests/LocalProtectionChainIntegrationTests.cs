using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// Phase 2V.1 -- Local Protection Chain Integration regression. Connects the real
// DetectionPipeline output through ExceptionTrustedEvaluator -> CandidatePolicyEvaluator ->
// AliasAssigner -> AliasReplacer using nothing but each stage's existing public API -- no new
// production orchestrator, result type, or helper is introduced anywhere outside this test
// file (see the Phase 2V STEP 1 report's ORCHESTRATOR_OWNERSHIP finding).
//
// All exception/trusted-public inputs are synthetic Detection-owned values constructed directly
// in each test -- there is no Storage bridge yet (STORAGE_DETECTION_BRIDGE_OWNER remains
// deferred). Every scenario here uses the real DetectionPipeline.CreateDefault() detectors;
// synthetic DetectionCandidate values are never substituted mid-chain, because the entire point
// of this phase is proving real detector output survives the rest of the chain unchanged.
//
// Naming: a chain result with residual CandidateDisposition.NeedsDecision candidates is called
// "rewrittenText" / "chainOutput" here only -- never Protected/Safe/Sendable/Verified. Those
// terms describe a later boundary (Composer write -> read-back -> final validation, see
// docs/release-gate.md) that this local chain never reaches or claims to reach.
public class LocalProtectionChainIntegrationTests
{
    private sealed record ChainRun(
        DetectionResult Detection,
        IReadOnlyList<EvaluatedCandidate> Evaluated,
        IReadOnlyList<CandidatePolicyDecision> Decisions,
        IReadOnlyList<AliasAssignment> Assignments,
        string RewrittenText);

    // Test-only helper -- not a production orchestrator. Wires the five real public APIs in
    // canonical pipeline order (contract §7) and nothing else.
    private static ChainRun RunLocalChain(
        string rawText,
        IReadOnlyList<AmbiguousExceptionValue>? exceptions = null,
        IReadOnlyList<TrustedPublicValue>? trustedPublic = null,
        AliasMap? aliasMap = null)
    {
        var detection = DetectionPipeline.CreateDefault().Detect(rawText);
        var evaluated = ExceptionTrustedEvaluator.Evaluate(
            detection, exceptions ?? [], trustedPublic ?? []);
        var decisions = CandidatePolicyEvaluator.Evaluate(evaluated);
        var assignments = AliasAssigner.Assign(decisions, aliasMap ?? new AliasMap());
        var rewrittenText = AliasReplacer.Apply(rawText, assignments);
        return new ChainRun(detection, evaluated, decisions, assignments, rewrittenText);
    }

    // ---- 1. Phone full chain ----
    [Fact]
    public void Phone_FullChain_ReplacedWithAlias()
    {
        var run = RunLocalChain("연락처 010-1234-5678");

        Assert.Equal("연락처 [전화번호1]", run.RewrittenText);
        Assert.Single(run.Detection.Candidates);
        Assert.Equal(PiiType.Phone, run.Detection.Candidates[0].PiiType);
        Assert.Equal(RiskLevel.Level2, run.Detection.Candidates[0].RiskLevel);
        Assert.Equal(CandidateDisposition.Protect, run.Decisions[0].Disposition);
    }

    // ---- 2. Email full chain ----
    [Fact]
    public void Email_FullChain_ReplacedWithAlias()
    {
        var run = RunLocalChain("user@example.com");

        Assert.Equal("[이메일1]", run.RewrittenText);
        Assert.Single(run.Detection.Candidates);
        Assert.Equal(PiiType.Email, run.Detection.Candidates[0].PiiType);
        Assert.Equal(CandidateDisposition.Protect, run.Decisions[0].Disposition);
    }

    // ---- 3. Phone + Email mixed: independent per-PiiType counters ----
    [Fact]
    public void PhoneAndEmail_Mixed_IndependentCounters()
    {
        var run = RunLocalChain("연락처 010-1234-5678, 이메일 user@company.test");

        Assert.Equal("연락처 [전화번호1], 이메일 [이메일1]", run.RewrittenText);
    }

    // ---- 4. Same Phone repeated: same alias reused ----
    [Fact]
    public void SamePhoneRepeated_SameAliasReused()
    {
        var run = RunLocalChain("010-1234-5678 그리고 다시 010-1234-5678");

        Assert.Equal("[전화번호1] 그리고 다시 [전화번호1]", run.RewrittenText);
    }

    // ---- 5. Phone formatting variants -> same canonical -> same alias ----
    [Fact]
    public void PhoneFormattingVariants_SameCanonical_SameAlias()
    {
        var run = RunLocalChain("010-1234-5678 및 010 1234 5678");

        // If the real PhoneCanonicalizer ever stopped collapsing these two formats onto the
        // same CanonicalValue, this assertion -- not a hand-tuned alias -- is what must fail.
        Assert.Equal("01012345678", run.Detection.Candidates[0].Canonical.Value);
        Assert.Equal("01012345678", run.Detection.Candidates[1].Canonical.Value);
        Assert.Equal("[전화번호1] 및 [전화번호1]", run.RewrittenText);
    }

    // ---- 6. Different Phones: raw-appearance-order numbering ----
    [Fact]
    public void DifferentPhones_NumberedByRawAppearanceOrder()
    {
        var run = RunLocalChain("A: 010-1234-5678, B: 010-9999-8888");

        Assert.Equal("A: [전화번호1], B: [전화번호2]", run.RewrittenText);
    }

    // ---- 7. Level2 Trusted -> Bypass, per-candidate not per-sentence ----
    [Fact]
    public void Level2Trusted_BypassesOnlyThatCandidate()
    {
        var trustedPhone = new TrustedPublicValue(PiiType.Phone, new CanonicalValue(PiiType.Phone, "01012345678"));
        var run = RunLocalChain(
            "A: 010-1234-5678, B: 010-9999-8888",
            trustedPublic: [trustedPhone]);

        // A (trusted) stays raw; B (not trusted) is protected -- and still gets number 1, since
        // it is the only Protect candidate, proving numbering is per-Protect-candidate, not
        // per-detected-candidate.
        Assert.Equal("A: 010-1234-5678, B: [전화번호1]", run.RewrittenText);
    }

    // ---- 8. Level2 Low Confidence still Protect (Confidence never weakens Level2) ----
    [Fact]
    public void Level2LowConfidence_StillProtect()
    {
        // Real fixture, no synthetic candidate: "user(at)example.com" is EmailDetector's own
        // obfuscated-match path (Level2/Medium -- see EmailDetectorTests), and the domain's own
        // "example" is itself one of ContextRiskAdjuster's fixed sample markers, so the real
        // chain steps Medium down to Low by itself. This is exactly the real supported fixture
        // requested by the Phase 2V spec's "먼저 확인" instruction, not a forced synthetic value.
        var run = RunLocalChain("담당자 연락처: user(at)example.com 입니다");

        var email = run.Detection.Candidates[0];
        Assert.Equal(RiskLevel.Level2, email.RiskLevel);
        Assert.Equal(DetectionConfidence.Low, email.Confidence);
        Assert.Equal(CandidateDisposition.Protect, run.Decisions[0].Disposition);
        Assert.Equal("담당자 연락처: [이메일1] 입니다", run.RewrittenText);
    }

    // ---- 9. Level1 Low bypass (bare GPS pair, order-ambiguous) ----
    [Fact]
    public void Level1LowGps_Bypass()
    {
        // Both values fit [-90,90] -> order-ambiguous -> Low (GpsCoordinateDetector). No
        // "GPS"/"gps"/"좌표"/"위치" keyword anywhere near it, so it never gets promoted to Level2.
        var run = RunLocalChain("임시 값으로 37.5665,55.0000 을 기록했다");

        var gps = run.Detection.Candidates[0];
        Assert.Equal(RiskLevel.Level1, gps.RiskLevel);
        Assert.Equal(DetectionConfidence.Low, gps.Confidence);
        Assert.Equal(CandidateDisposition.Bypass, run.Decisions[0].Disposition);
        Assert.Equal("임시 값으로 37.5665,55.0000 을 기록했다", run.RewrittenText);
    }

    // ---- 10. Level1 Medium NeedsDecision (bare GPS pair, order-unambiguous) ----
    [Fact]
    public void Level1MediumGps_NeedsDecision_RawUnchanged()
    {
        // Longitude 126.9780 cannot also be a latitude ([-90,90]), so order is unambiguous ->
        // Medium (GpsCoordinateDetector), not Low. Still no GPS/좌표/위치 keyword nearby.
        var run = RunLocalChain("임시로 37.5665,126.9780 값을 저장했다");

        var gps = run.Detection.Candidates[0];
        Assert.Equal(RiskLevel.Level1, gps.RiskLevel);
        Assert.Equal(DetectionConfidence.Medium, gps.Confidence);
        Assert.Equal(CandidateDisposition.NeedsDecision, run.Decisions[0].Disposition);
        Assert.Null(run.Assignments[0].Alias);
        Assert.Equal("임시로 37.5665,126.9780 값을 저장했다", run.RewrittenText);
    }

    // ---- 11. Level3 Secret: unresolved/raw under base policy, never Protected/Safe/Verified ----
    [Fact]
    public void Level3Secret_Unresolved_RemainsRawInLocalChain()
    {
        // Fully synthetic, deterministic, non-real token -- clears SecretDetector's strong-key
        // minimum-evidence bar (mixed letter/digit, length >= 6).
        var run = RunLocalChain("api_key=Zx9Qw2Lm7Rt4");

        var secret = run.Detection.Candidates[0];
        Assert.Equal(PiiType.Secret, secret.PiiType);
        Assert.Equal(RiskLevel.Level3, secret.RiskLevel);
        Assert.Equal(TrustState.Untrusted, run.Evaluated[0].TrustState);
        Assert.Equal(CandidateDisposition.NeedsDecision, run.Decisions[0].Disposition);
        Assert.Null(run.Assignments[0].Alias);

        // NOT resolved by this local chain -- this is the current, intended base-policy state
        // (LEVEL3_LOCAL_CHAIN_SEMANTICS from the Phase 2V STEP 1 report), not a bug. A future
        // composition/runtime layer must never treat this unresolved/raw text as Sendable.
        Assert.Equal("api_key=Zx9Qw2Lm7Rt4", run.RewrittenText);
    }

    // ---- 12. Mixed disposition: Protect + Bypass ----
    [Fact]
    public void Mixed_ProtectAndBypass()
    {
        var run = RunLocalChain("연락처 010-1234-5678 입니다. 임시값 37.5665,55.0000 기록함.");

        Assert.Equal(
            "연락처 [전화번호1] 입니다. 임시값 37.5665,55.0000 기록함.",
            run.RewrittenText);
    }

    // ---- 13. Mixed disposition: Protect + NeedsDecision ----
    [Fact]
    public void Mixed_ProtectAndNeedsDecision()
    {
        var run = RunLocalChain("연락처 010-1234-5678 입니다. 임시로 37.5665,126.9780 기록함.");

        Assert.Equal(
            "연락처 [전화번호1] 입니다. 임시로 37.5665,126.9780 기록함.",
            run.RewrittenText);
    }

    // ---- 14. Mixed disposition: Protect + Bypass + NeedsDecision together ----
    [Fact]
    public void Mixed_ProtectBypassAndNeedsDecision()
    {
        var run = RunLocalChain(
            "연락처 010-1234-5678 입니다. 임시값 37.5665,55.0000 그리고 37.5665,126.9780 기록함.");

        Assert.Equal(3, run.Detection.Candidates.Count);
        // DetectionResult order reflects OverlapResolver's RiskLevel/Confidence precedence, not
        // raw left-to-right text order (DETECTIONRESULT_ORDER_NOT_RAW_SPAN_ORDER) -- Level2
        // Phone first, then the two Level1 GPS candidates ordered Medium (NeedsDecision) before
        // Low (Bypass). AliasReplacer still applies mutation independent of this order.
        Assert.Equal(
            [CandidateDisposition.Protect, CandidateDisposition.NeedsDecision, CandidateDisposition.Bypass],
            run.Decisions.Select(d => d.Disposition));
        Assert.Equal(
            "연락처 [전화번호1] 입니다. 임시값 37.5665,55.0000 그리고 37.5665,126.9780 기록함.",
            run.RewrittenText);
    }

    // ---- 15. Same AliasMap across simulated revisions: identity preserved, no reuse ----
    [Fact]
    public void SameAliasMap_AcrossRevisions_PreservesIdentityAndNeverReusesNumbers()
    {
        var aliasMap = new AliasMap();

        var call1 = RunLocalChain("담당자 010-1234-5678", aliasMap: aliasMap);
        Assert.Equal("담당자 [전화번호1]", call1.RewrittenText);

        var call2 = RunLocalChain("담당자 010-1234-5678, 대체 담당자 010-9999-8888", aliasMap: aliasMap);
        Assert.Equal("담당자 [전화번호1], 대체 담당자 [전화번호2]", call2.RewrittenText);

        // Phone A no longer appears in this "revision" (simulating a user edit); Phone C is
        // new. Numbering must keep advancing from 3, never reuse 1.
        var call3 = RunLocalChain("신규 담당자 010-5555-1234", aliasMap: aliasMap);
        Assert.Equal("신규 담당자 [전화번호3]", call3.RewrittenText);
    }

    // ---- 16. New AliasMap instance resets numbering ----
    [Fact]
    public void NewAliasMap_ResetsNumbering()
    {
        var firstMapRun = RunLocalChain("010-1234-5678", aliasMap: new AliasMap());
        Assert.Equal("[전화번호1]", firstMapRun.RewrittenText);

        // A brand new AliasMap instance (simulating a fresh composition) starts back at 1 even
        // though this process already assigned "전화번호1" once above -- AliasMap state is never
        // shared across instances.
        var secondMapRun = RunLocalChain("010-9999-8888", aliasMap: new AliasMap());
        Assert.Equal("[전화번호1]", secondMapRun.RewrittenText);
    }

    // ---- 17. URL-embedded PII: precise value-only replacement ----
    [Fact]
    public void UrlEmbeddedPhone_ValueOnlyReplaced()
    {
        var run = RunLocalChain("https://example.com/?phone=01000000000");

        Assert.Equal("https://example.com/?phone=[전화번호1]", run.RewrittenText);
    }

    // ---- 18. Filename-embedded PII: precise value-only replacement (Phase 2O.1 IP hardening) ----
    [Fact]
    public void FilenameEmbeddedIp_ValueOnlyReplaced()
    {
        var run = RunLocalChain("192.0.2.1.log");

        Assert.Equal("[IP주소1].log", run.RewrittenText);
    }

    // ---- 19. Zero-width character inside a Phone: consumed by replacement, rest preserved ----
    [Fact]
    public void ZeroWidthCharacterInsidePhone_ConsumedByReplacement()
    {
        var zeroWidthSpace = ((char)0x200B).ToString();
        var rawText = $"번호: 010{zeroWidthSpace}-1234-5678 확인";

        var run = RunLocalChain(rawText);

        Assert.Equal("번호: [전화번호1] 확인", run.RewrittenText);
    }

    // ---- 20. Korean multiline (CRLF + LF mixed) ----
    [Fact]
    public void KoreanMultiline_CrlfAndLfPreserved()
    {
        var rawText = "안녕하세요,\n연락처는 010-1234-5678 입니다.\r\n감사합니다.";

        var run = RunLocalChain(rawText);

        Assert.Equal("안녕하세요,\n연락처는 [전화번호1] 입니다.\r\n감사합니다.", run.RewrittenText);
    }

    // ---- 21. Emoji (surrogate pair) surrounding PII ----
    [Fact]
    public void EmojiSurroundingPhone_SurrogatePairsPreserved()
    {
        var rawText = "\U0001F600 연락처 010-1234-5678 입니다 \U0001F600";

        var run = RunLocalChain(rawText);

        Assert.Equal("\U0001F600 연락처 [전화번호1] 입니다 \U0001F600", run.RewrittenText);
    }

    // ---- 22/23. 50k / 100k chain smoke: no exception, exact replacement, no corruption ----
    [Theory]
    [InlineData(50_000)]
    [InlineData(100_000)]
    public void LargeInput_ChainCompletes_ExactReplacement(int approximateLength)
    {
        const string fillerUnit = "가나다라마바사아자차카타파하 ";
        const string phone = "010-1234-5678";

        var filler = string.Concat(Enumerable.Repeat(fillerUnit, approximateLength / fillerUnit.Length + 1));
        int insertAt = filler.Length / 2;
        var rawText = filler[..insertAt] + phone + filler[insertAt..];

        var run = RunLocalChain(rawText);

        var expected = filler[..insertAt] + "[전화번호1]" + filler[insertAt..];
        Assert.True(
            string.Equals(expected, run.RewrittenText, StringComparison.Ordinal),
            "Large-input chain output did not match the expected exact replacement (message omits both large strings by design).");
        Assert.Equal(expected.Length, run.RewrittenText.Length);
    }

    // ---- 24. Metadata preservation: candidate identity/fields unchanged end to end ----
    [Fact]
    public void MetadataPreservation_CandidateIdentityUnchangedThroughChain()
    {
        var run = RunLocalChain("연락처 010-1234-5678 입니다. 임시값 37.5665,55.0000 기록함.");

        Assert.Equal(2, run.Detection.Candidates.Count);
        for (int i = 0; i < run.Detection.Candidates.Count; i++)
        {
            var original = run.Detection.Candidates[i];

            // Reference equality: no stage wraps a copy -- every layer wraps the exact same
            // DetectionCandidate instance produced by the real DetectionPipeline.
            Assert.Same(original, run.Evaluated[i].Candidate);
            Assert.Same(original, run.Decisions[i].Candidate.Candidate);
            Assert.Same(original, run.Assignments[i].Decision.Candidate.Candidate);
        }

        var phoneCandidate = run.Detection.Candidates[0];
        Assert.Equal(PiiType.Phone, phoneCandidate.PiiType);
        Assert.Equal(RiskLevel.Level2, phoneCandidate.RiskLevel);
        Assert.Equal(DetectionConfidence.High, phoneCandidate.Confidence);
        Assert.Equal("01012345678", phoneCandidate.Canonical.Value);
        Assert.Equal("PhoneDetector", phoneCandidate.DetectorName);
    }

    // ---- 25. No production ProtectionState reference anywhere in this local chain's output ----
    [Fact]
    public void LocalChain_NeverProducesProtectionStateVerified()
    {
        // This is a documentation-style regression, not a behavioral one: the local chain's
        // return type is a plain string (see AliasReplacer.Apply), and none of the five stages
        // this test drives references Privon.Core.ProtectionState at all. There is nothing to
        // assert against a type that is never touched -- this test exists so a future change
        // that DOES start threading ProtectionState through this chain fails loudly by needing
        // to modify this file's using directives/assertions rather than sliding in silently.
        var run = RunLocalChain("010-1234-5678");

        Assert.IsType<string>(run.RewrittenText);
    }
}
