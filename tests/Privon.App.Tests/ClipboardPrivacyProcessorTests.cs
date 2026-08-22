using System.Reflection;
using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Windows;

namespace Privon.App.Tests;

// Phase 3B STEP4 -- ClipboardPrivacyProcessor regression, extended in Phase 3B STEP8 with real
// trust/exception evaluation and Phase 3B STEP10 with real base candidate policy. Unlike
// ClipboardPrivacyCoordinatorTests (which uses FakeClipboardPrivacyProcessor to keep the
// dispatch/target-gate boundary OS-free and Detection-free), every test here uses the REAL
// production ClipboardPrivacyProcessor, backed by the REAL DetectionPipeline.CreateDefault(), the
// REAL ExceptionTrustedEvaluator, and the REAL CandidatePolicyEvaluator -- proving the
// integration boundary is genuine, not a fabricated DetectionResult/EvaluatedCandidate/
// CandidatePolicyDecision standing in for it. Only the trust/exception PROVIDER is a hand-written
// fake (FakeTrustExceptionProvider) -- real PrivonLocalStore-backed mapping already has its own
// dedicated coverage in TrustExceptionProviderTests.cs (Phase 3B STEP6). Detailed detector/
// evaluator/policy correctness (regex edge cases, canonicalization, overlap resolution, the full
// 27-row Level/Confidence/TrustState policy matrix) remains owned by Privon.Detection.Tests --
// this file only needs enough real-chain coverage to prove App's own wiring (load timing, load
// count, argument order, disposition counting, failure propagation) is genuine.
//
// REAL_CHAIN_COVERAGE_GAP (Phase 3B STEP10, reported per instruction rather than fabricated):
// RiskLevel.Level1 is produced ONLY by GpsCoordinateDetector (confirmed by source inspection --
// no other detector ever assigns Level1), and that detector never assigns
// DetectionConfidence.High at Level1 (only Medium for an order-unambiguous bare pair, Low for an
// order-ambiguous one -- see GpsCoordinateDetector's own doc). The base-policy row
// "Level1 + Untrusted/Unknown + High -> Protect" is therefore NOT reachable through the real
// default DetectionPipeline and is deliberately NOT covered by an App-level test here -- it
// remains exhaustively covered (along with all 27 rows) by
// Privon.Detection.Tests.CandidatePolicyEvaluatorTests.FullPolicyMatrix_MatchesConfirmedTable.
public class ClipboardPrivacyProcessorTests
{
    private static readonly ForegroundTargetSnapshot ChatGpt =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT");

    private static ClipboardTextSnapshot Snapshot(string text) =>
        new(SequenceNumber: 1, HasReliableSequence: true, Text: text);

    // A deliberately-throwing real IDetector -- the smallest seam that proves
    // DETECTION_FAILURE_POLICY without inventing a delegate-shaped abstraction over Detection.
    // Only Privon.Detection's own public IDetector is needed; no InternalsVisibleTo required.
    private sealed class ThrowingDetector : IDetector
    {
        public string Name => "ThrowingDetector";
        public PiiType PiiType => PiiType.Secret;
        public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context) =>
            throw new InvalidOperationException("synthetic detector failure for ClipboardPrivacyProcessor test");
    }

    // Runs the REAL default pipeline once to discover a fixture's actual RiskLevel/Confidence/
    // CanonicalValue rather than guessing canonicalizer/detector output -- asserts the fixture
    // still produces exactly the single candidate at the expected (RiskLevel, Confidence) the
    // test needs, so a future detector change breaks loudly here instead of silently invalidating
    // the trust/exception match or the expected disposition below.
    private static CanonicalValue DiscoverCanonical(
        string rawText, PiiType expectedType, RiskLevel expectedLevel, DetectionConfidence? expectedConfidence = null)
    {
        var probe = DetectionPipeline.CreateDefault().Detect(rawText);
        var candidate = Assert.Single(probe.Candidates);
        Assert.Equal(expectedType, candidate.PiiType);
        Assert.Equal(expectedLevel, candidate.RiskLevel);
        if (expectedConfidence is { } conf) Assert.Equal(conf, candidate.Confidence);
        return candidate.Canonical;
    }

    // ==================================================================
    // REAL PIPELINE (section 20/17, items 1-6 from Phase 3B STEP4)
    // ==================================================================

    // ---- 1. no-PII synthetic text -> CandidateCount == 0 ----
    [Fact]
    public void Process_NoPiiText_CandidateCountZero()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot("오늘 날씨가 좋네요"));
        var result = outcome.Result;

        Assert.Equal(0, result.CandidateCount);
        Assert.Equal(0, result.TrustedCount);
        Assert.False(result.HasDetectedCandidates);
        Assert.Null(outcome.WritePlan);
    }

    // ---- 2. synthetic Korean phone -> CandidateCount > 0 ----
    [Fact]
    public void Process_KoreanPhone_CandidateCountPositive()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot("연락처 010-1234-5678"));
        var result = outcome.Result;

        Assert.True(result.CandidateCount > 0);
        Assert.True(result.HasDetectedCandidates);
        // Level2 + Untrusted -> Protect regardless of confidence -> a write plan is produced.
        Assert.NotNull(outcome.WritePlan);
    }

    // ---- 3. synthetic email -> CandidateCount > 0 ----
    [Fact]
    public void Process_Email_CandidateCountPositive()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot("user@example.test"));

        Assert.True(outcome.Result.CandidateCount > 0);
    }

    // ---- 4. synthetic RRN (Level3) -> CandidateCount > 0 ----
    [Fact]
    public void Process_ResidentRegistrationNumber_CandidateCountPositive()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        // Synthetic, structurally-plausible RRN -- not a real person's number.
        var outcome = processor.Process(ChatGpt, Snapshot("주민번호 901231-1234567"));

        Assert.True(outcome.Result.CandidateCount > 0);
        // Level3 -> always NeedsDecision -> NEEDSDECISION_BLOCKS_REPLACEMENT -> no plan.
        Assert.Null(outcome.WritePlan);
    }

    // ---- 5/10. mixed synthetic PII -> CandidateCount reflects multiple candidates, and (Phase 3B
    // STEP10) both Level2 candidates land as Protect since neither is trusted -- also proves the
    // disposition-count sum invariant (item 9) on a real multi-candidate result ----
    [Fact]
    public void Process_MixedPii_CandidateCountReflectsMultiple()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot("연락처 010-1234-5678, 이메일 user@company.test"));
        var result = outcome.Result;

        Assert.True(result.CandidateCount >= 2);
        Assert.Equal(0, result.TrustedCount);
        Assert.Equal(result.CandidateCount, result.ProtectCount); // both Level2, both Untrusted -> both Protect
        Assert.Equal(0, result.NeedsDecisionCount);
        Assert.Equal(0, result.BypassCount);
        Assert.Equal(result.CandidateCount, result.ProtectCount + result.NeedsDecisionCount + result.BypassCount);
        // NeedsDecisionCount == 0 && ProtectCount > 0 -> a write plan is produced.
        Assert.NotNull(outcome.WritePlan);
    }

    // ---- 6. Korean + multiline + emoji fixture -> detection completes correctly ----
    [Fact]
    public void Process_KoreanMultilineEmojiFixture_DetectsExpectedCandidate()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot("안녕하세요 😀\n연락처: 010-1234-5678\n감사합니다 🙏"));

        Assert.True(outcome.Result.CandidateCount > 0);
    }

    // ---- structural companion to 1-6: the production constructor always builds exactly one real
    // default pipeline -- confirmed by two independent Process calls on the SAME instance both
    // detecting real PII (a fresh/empty/misconfigured pipeline would not) ----
    [Fact]
    public void Process_SameInstance_ReusedAcrossMultipleCalls_StillDetectsCorrectly()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var first = processor.Process(ChatGpt, Snapshot("010-1234-5678"));
        var second = processor.Process(ChatGpt, Snapshot("아무 정보 없음"));
        var third = processor.Process(ChatGpt, Snapshot("user@example.test"));

        Assert.True(first.Result.CandidateCount > 0);
        Assert.Equal(0, second.Result.CandidateCount);
        Assert.True(third.Result.CandidateCount > 0);
    }

    // ==================================================================
    // DETECTION_FAILURE_POLICY (section 8/12, Phase 3B STEP4)
    // ==================================================================

    // ---- Detect() throwing propagates out of Process() unmodified -- never reinterpreted as
    // CandidateCount == 0 ("no PII"), and never wrapped with any text-bearing content. Policy is
    // never reached: there is no metadata result of any kind to inspect. ----
    [Fact]
    public void Process_DetectionThrows_PropagatesUnmodified_NotReinterpretedAsNoPii()
    {
        var throwingPipeline = new DetectionPipeline([new ThrowingDetector()]);
        var processor = new ClipboardPrivacyProcessor(throwingPipeline, new FakeTrustExceptionProvider());

        var ex = Assert.Throws<InvalidOperationException>(
            () => processor.Process(ChatGpt, Snapshot("010-1234-5678")));

        Assert.Equal("synthetic detector failure for ClipboardPrivacyProcessor test", ex.Message);
        Assert.DoesNotContain("010-1234-5678", ex.Message);
    }

    // ==================================================================
    // NO_PII FAST PATH / PROVIDER LOAD COUNT (Phase 3B STEP8, section 4/5/16 items 1/10)
    // ==================================================================

    // ---- no PII -> provider is never loaded, and (Phase 3B STEP10) all five metadata counts are
    // zero, including the three new disposition counts ----
    [Fact]
    public void Process_NoPii_ProviderNeverLoaded_AllCountsZero()
    {
        var provider = new FakeTrustExceptionProvider();
        var processor = new ClipboardPrivacyProcessor(provider);

        var outcome = processor.Process(ChatGpt, Snapshot("오늘 날씨가 좋네요"));
        var result = outcome.Result;

        Assert.Equal(0, result.CandidateCount);
        Assert.Equal(0, result.TrustedCount);
        Assert.Equal(0, result.ProtectCount);
        Assert.Equal(0, result.NeedsDecisionCount);
        Assert.Equal(0, result.BypassCount);
        Assert.Equal(0, provider.LoadCount);
        Assert.Null(outcome.WritePlan);

        // (Phase 3B STEP12) the alias stage was never reached at all -- Assignments is null,
        // not an empty list, distinguishing "never ran" from "ran with nothing to do."
        var (_, assignments) = processor.ProcessWithAssignments(ChatGpt, Snapshot("오늘 날씨가 좋네요"));
        Assert.Null(assignments);
    }

    // ---- PII detected -> provider loaded exactly once (no cache, no duplicate load) ----
    [Fact]
    public void Process_PiiDetected_ProviderLoadedExactlyOnce()
    {
        var provider = new FakeTrustExceptionProvider();
        var processor = new ClipboardPrivacyProcessor(provider);

        processor.Process(ChatGpt, Snapshot("010-1234-5678"));

        Assert.Equal(1, provider.LoadCount);
    }

    // ==================================================================
    // TRUST/EXCEPTION EVALUATION + BASE POLICY -- REAL EVALUATORS
    // (Phase 3B STEP8 section 16 items 2-9, extended Phase 3B STEP10 section 20 items 2/4/6/8)
    // ==================================================================

    // ---- Level1 candidate + matching exception -> Trusted, and (STEP10) Level1+Trusted ->
    // Bypass under base policy regardless of confidence (item 2) ----
    [Fact]
    public void Process_Level1CandidateMatchingException_TrustedAndBypassed()
    {
        const string text = "37.5665,126.9780"; // bare, unlabeled GPS pair -> Level1
        var canonical = DiscoverCanonical(text, PiiType.GpsCoordinate, RiskLevel.Level1);
        var provider = new FakeTrustExceptionProvider(new TrustExceptionSnapshot(
            trustedPublic: [],
            exceptions: [new AmbiguousExceptionValue(canonical.PiiType, canonical)]));
        var processor = new ClipboardPrivacyProcessor(provider);

        var outcome = processor.Process(ChatGpt, Snapshot(text));
        var result = outcome.Result;

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(1, result.TrustedCount);
        Assert.Equal(1, result.BypassCount);
        Assert.Equal(0, result.ProtectCount);
        Assert.Equal(0, result.NeedsDecisionCount);
        // ProtectCount == 0 -> ALL_BYPASS -> no write plan.
        Assert.Null(outcome.WritePlan);
    }

    // ---- Level1 candidate + matching trusted-public ONLY -> remains Untrusted (also proves the
    // App wiring passes trust.Exceptions, not trust.TrustedPublic, as the evaluator's
    // "exceptions" argument -- a swap would incorrectly make this Trusted+Bypass). Under base
    // policy this fixture is Level1+Untrusted+Medium (order-unambiguous bare pair) -> (STEP10)
    // NeedsDecision (item 4). ----
    [Fact]
    public void Process_Level1CandidateMatchingTrustedPublicOnly_RemainsUntrusted_NeedsDecision()
    {
        const string text = "37.5665,126.9780";
        var canonical = DiscoverCanonical(text, PiiType.GpsCoordinate, RiskLevel.Level1, DetectionConfidence.Medium);
        var provider = new FakeTrustExceptionProvider(new TrustExceptionSnapshot(
            trustedPublic: [new TrustedPublicValue(canonical.PiiType, canonical)],
            exceptions: []));
        var processor = new ClipboardPrivacyProcessor(provider);

        var outcome = processor.Process(ChatGpt, Snapshot(text));
        var result = outcome.Result;

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(0, result.TrustedCount);
        Assert.Equal(1, result.NeedsDecisionCount);
        Assert.Equal(0, result.ProtectCount);
        Assert.Equal(0, result.BypassCount);
        // NeedsDecisionCount > 0 -> NEEDSDECISION_BLOCKS_REPLACEMENT -> no write plan.
        Assert.Null(outcome.WritePlan);
    }

    // ---- (STEP10, section 20 item 5) Level1 + Untrusted + Low confidence (order-ambiguous bare
    // pair, both values within [-90,90]) -> Bypass. This is the OTHER cause of Bypass besides
    // Trust -- proves BypassCount and TrustedCount are independent axes (item 11): here
    // BypassCount == 1 while TrustedCount == 0. ----
    [Fact]
    public void Process_Level1UntrustedLowConfidence_Bypasses_WithoutBeingTrusted()
    {
        const string text = "37.5665,55.0000"; // both values in [-90,90] -> order-ambiguous -> Low
        DiscoverCanonical(text, PiiType.GpsCoordinate, RiskLevel.Level1, DetectionConfidence.Low);
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider()); // empty snapshot -> Untrusted

        var outcome = processor.Process(ChatGpt, Snapshot(text));
        var result = outcome.Result;

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(0, result.TrustedCount);
        Assert.Equal(1, result.BypassCount);
        Assert.True(result.BypassCount > result.TrustedCount);
        Assert.Equal(0, result.ProtectCount);
        Assert.Equal(0, result.NeedsDecisionCount);
        Assert.Null(outcome.WritePlan);
    }

    // ---- Level2 candidate + matching trusted-public -> Trusted, and (STEP10) Level2+Trusted ->
    // Bypass regardless of confidence (item 6) ----
    [Fact]
    public void Process_Level2CandidateMatchingTrustedPublic_TrustedAndBypassed()
    {
        const string text = "010-1234-5678";
        var canonical = DiscoverCanonical(text, PiiType.Phone, RiskLevel.Level2);
        var provider = new FakeTrustExceptionProvider(new TrustExceptionSnapshot(
            trustedPublic: [new TrustedPublicValue(canonical.PiiType, canonical)],
            exceptions: []));
        var processor = new ClipboardPrivacyProcessor(provider);

        var outcome = processor.Process(ChatGpt, Snapshot(text));
        var result = outcome.Result;

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(1, result.TrustedCount);
        Assert.Equal(1, result.BypassCount);
        Assert.Equal(0, result.ProtectCount);
        Assert.Equal(0, result.NeedsDecisionCount);
        Assert.Null(outcome.WritePlan);
    }

    // ---- Level2 candidate + matching exception ONLY -> remains Untrusted (mirrors the test
    // above in the other direction -- proves trust.TrustedPublic, not trust.Exceptions, is passed
    // as the evaluator's "trustedPublic" argument). Level2+Untrusted -> (STEP10) Protect
    // regardless of confidence (item 7). ----
    [Fact]
    public void Process_Level2CandidateMatchingExceptionOnly_RemainsUntrusted_Protects()
    {
        const string text = "010-1234-5678";
        var canonical = DiscoverCanonical(text, PiiType.Phone, RiskLevel.Level2);
        var provider = new FakeTrustExceptionProvider(new TrustExceptionSnapshot(
            trustedPublic: [],
            exceptions: [new AmbiguousExceptionValue(canonical.PiiType, canonical)]));
        var processor = new ClipboardPrivacyProcessor(provider);

        var outcome = processor.Process(ChatGpt, Snapshot(text));
        var result = outcome.Result;

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(0, result.TrustedCount);
        Assert.Equal(1, result.ProtectCount);
        Assert.Equal(0, result.NeedsDecisionCount);
        Assert.Equal(0, result.BypassCount);
        // Protect-only (item 5, Phase 3B STEP14): a plan is produced with the raw value replaced.
        Assert.NotNull(outcome.WritePlan);
        Assert.Equal("[전화번호1]", outcome.WritePlan!.ReplacementText);
        Assert.DoesNotContain(text, outcome.WritePlan.ReplacementText);
    }

    // ---- Level3 candidate + matching BOTH lists -> remains Untrusted (Level3 is never evaluated
    // against either list, regardless of matches), and (STEP10) Level3 -> always NeedsDecision
    // under base policy, even with matches in both lists (item 8) ----
    [Fact]
    public void Process_Level3CandidateMatchingBothLists_RemainsUntrusted_AlwaysNeedsDecision()
    {
        const string text = "901231-1234567";
        var canonical = DiscoverCanonical(text, PiiType.ResidentRegistrationNumber, RiskLevel.Level3);
        var provider = new FakeTrustExceptionProvider(new TrustExceptionSnapshot(
            trustedPublic: [new TrustedPublicValue(canonical.PiiType, canonical)],
            exceptions: [new AmbiguousExceptionValue(canonical.PiiType, canonical)]));
        var processor = new ClipboardPrivacyProcessor(provider);

        var outcome = processor.Process(ChatGpt, Snapshot(text));
        var result = outcome.Result;

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(0, result.TrustedCount);
        Assert.Equal(1, result.NeedsDecisionCount);
        Assert.Equal(0, result.ProtectCount);
        Assert.Equal(0, result.BypassCount);
        Assert.Null(outcome.WritePlan);
    }

    // ---- non-matching canonical value -> Untrusted ----
    [Fact]
    public void Process_NonMatchingCanonicalValue_RemainsUntrusted()
    {
        const string text = "010-1234-5678";
        var canonical = DiscoverCanonical(text, PiiType.Phone, RiskLevel.Level2);
        var different = new CanonicalValue(canonical.PiiType, canonical.Value + "9");
        var provider = new FakeTrustExceptionProvider(new TrustExceptionSnapshot(
            trustedPublic: [new TrustedPublicValue(different.PiiType, different)],
            exceptions: []));
        var processor = new ClipboardPrivacyProcessor(provider);

        var outcome = processor.Process(ChatGpt, Snapshot(text));

        Assert.Equal(0, outcome.Result.TrustedCount);
    }

    // ---- same canonical string value but registered under the wrong PiiType -> Untrusted ----
    [Fact]
    public void Process_SameCanonicalValueUnderWrongPiiType_RemainsUntrusted()
    {
        const string text = "010-1234-5678";
        var canonical = DiscoverCanonical(text, PiiType.Phone, RiskLevel.Level2);
        var wrongType = new CanonicalValue(PiiType.BankAccountNumber, canonical.Value);
        var provider = new FakeTrustExceptionProvider(new TrustExceptionSnapshot(
            trustedPublic: [new TrustedPublicValue(PiiType.BankAccountNumber, wrongType)],
            exceptions: []));
        var processor = new ClipboardPrivacyProcessor(provider);

        var outcome = processor.Process(ChatGpt, Snapshot(text));

        Assert.Equal(0, outcome.Result.TrustedCount);
    }

    // ---- empty TrustExceptionSnapshot -> all detected candidates remain Untrusted ----
    [Fact]
    public void Process_EmptyTrustExceptionSnapshot_AllCandidatesUntrusted()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot("연락처 010-1234-5678, 이메일 user@company.test"));

        Assert.True(outcome.Result.CandidateCount >= 2);
        Assert.Equal(0, outcome.Result.TrustedCount);
    }

    // ==================================================================
    // APP_TRUST_PROVIDER_FAILURE_POLICY (Phase 3B STEP8, section 9/18)
    // ==================================================================

    // ---- unexpected provider failure propagates unmodified -- never substituted with an empty
    // snapshot, never reinterpreted as a "clean" metadata result. Policy is never reached. ----
    [Fact]
    public void Process_ProviderThrows_PropagatesUnmodified_NoCleanResultReturned()
    {
        var provider = new FakeTrustExceptionProvider(new InvalidOperationException("synthetic provider failure"));
        var processor = new ClipboardPrivacyProcessor(provider);

        var ex = Assert.Throws<InvalidOperationException>(
            () => processor.Process(ChatGpt, Snapshot("010-1234-5678")));

        Assert.Equal("synthetic provider failure", ex.Message);
    }

    // ---- a provider configured to throw is never even reached on the no-PII path -- reinforces
    // Process_NoPii_ProviderNeverLoaded_AllCountsZero from the failure-injection side ----
    [Fact]
    public void Process_ProviderConfiguredToThrow_NoPii_NeverCalled_NoThrow()
    {
        var provider = new FakeTrustExceptionProvider(new InvalidOperationException("should never be called"));
        var processor = new ClipboardPrivacyProcessor(provider);

        var outcome = processor.Process(ChatGpt, Snapshot("오늘 날씨가 좋네요"));

        Assert.Equal(0, outcome.Result.CandidateCount);
        Assert.Equal(0, provider.LoadCount);
    }

    // ==================================================================
    // RAW_REFERENCE_LIFETIME / structural
    // (Phase 3B STEP4 section 23, extended STEP8/STEP10)
    // ==================================================================

    // ---- ClipboardPrivacyProcessor retains no raw/sensitive state between calls -- the only
    // persistent fields are the reused DetectionPipeline and the injected ITrustExceptionProvider
    // (infrastructure, not sensitive data itself); no field of CandidatePolicyDecision either ----
    [Fact]
    public void ClipboardPrivacyProcessor_HasNoRawOrSensitiveInstanceField()
    {
        var fields = typeof(ClipboardPrivacyProcessor)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        var forbiddenTypes = new[]
        {
            typeof(string), typeof(ClipboardTextSnapshot), typeof(DetectionResult),
            typeof(DetectionCandidate), typeof(CanonicalValue), typeof(TrustExceptionSnapshot),
            typeof(TrustedPublicValue), typeof(AmbiguousExceptionValue),
            typeof(IReadOnlyList<EvaluatedCandidate>), typeof(IReadOnlyList<CandidatePolicyDecision>),
            typeof(AliasMap), typeof(IReadOnlyList<AliasAssignment>), typeof(AliasToken),
            typeof(ClipboardWritePlan), typeof(ClipboardPrivacyProcessingOutcome),
            typeof(ClipboardDecisionPlan), typeof(ClipboardDecisionItem), typeof(IReadOnlyList<ClipboardDecisionItem>),
        };

        Assert.DoesNotContain(fields, f => forbiddenTypes.Contains(f.FieldType));
        // The two allowed persistent fields: the reused DetectionPipeline and the injected
        // trust/exception provider seam -- both infrastructure, neither sensitive data.
        Assert.Contains(fields, f => f.FieldType == typeof(DetectionPipeline));
        Assert.Contains(fields, f => f.FieldType == typeof(ITrustExceptionProvider));
    }

    // ---- no synthetic sentinel raw text ever surfaces from a completed Process() call -- proven
    // both by the metadata-only return type (structural, see below) and by never having wrapped
    // it into an exception message on the failure path (see the DETECTION_FAILURE_POLICY test
    // above) ----
    [Fact]
    public void Process_RealSentinelText_NeverAppearsInResult()
    {
        const string sentinel = "RAW-APP-DETECTION-SENTINEL-518203";
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot($"{sentinel} 010-1234-5678"));

        // ProtectCount > 0 / NeedsDecisionCount == 0 here -> a WritePlan is produced whose
        // ReplacementText literally still contains the sentinel (only the phone span is
        // replaced) -- proving the DIAGNOSTIC surface (ToString/interpolation), not the plan's
        // own text field, never exposes it.
        Assert.NotNull(outcome.WritePlan);
        Assert.DoesNotContain(sentinel, outcome.ToString());
        Assert.DoesNotContain(sentinel, $"{outcome}");
        Assert.DoesNotContain(sentinel, outcome.WritePlan!.ToString());
        Assert.DoesNotContain(sentinel, $"{outcome.WritePlan}");
    }

    // ==================================================================
    // METADATA RESULT DIAGNOSTICS
    // (Phase 3B STEP4 section 22, extended STEP8/STEP10)
    // ==================================================================

    private static ClipboardPrivacyProcessingResult MakeResult(
        int candidateCount = 3, int trustedCount = 1, int protectCount = 1, int needsDecisionCount = 1, int bypassCount = 1) =>
        new(candidateCount, trustedCount, protectCount, needsDecisionCount, bypassCount);

    [Fact]
    public void ClipboardPrivacyProcessingResult_ToString_DoesNotContainSentinel()
    {
        const string sentinel = "RAW-APP-DETECTION-SENTINEL-518203";
        var result = MakeResult();

        // The sentinel was never a constructor input in the first place -- this is a structural
        // sanity check that the type's own ToString only ever renders counts, which is reinforced
        // by the "structurally cannot carry such content" reflection test below.
        Assert.DoesNotContain(sentinel, result.ToString());
    }

    [Fact]
    public void ClipboardPrivacyProcessingResult_ToString_ContainsAllFiveCounts()
    {
        var result = new ClipboardPrivacyProcessingResult(
            CandidateCount: 5, TrustedCount: 1, ProtectCount: 2, NeedsDecisionCount: 1, BypassCount: 2);
        string rendered = result.ToString();

        Assert.Contains("CandidateCount", rendered);
        Assert.Contains("TrustedCount", rendered);
        Assert.Contains("ProtectCount", rendered);
        Assert.Contains("NeedsDecisionCount", rendered);
        Assert.Contains("BypassCount", rendered);
    }

    [Fact]
    public void ClipboardPrivacyProcessingResult_HasDetectedCandidates_ReflectsCount()
    {
        Assert.False(new ClipboardPrivacyProcessingResult(0, 0, 0, 0, 0).HasDetectedCandidates);
        Assert.True(new ClipboardPrivacyProcessingResult(1, 0, 1, 0, 0).HasDetectedCandidates);
        Assert.True(new ClipboardPrivacyProcessingResult(5, 2, 2, 1, 2).HasDetectedCandidates);
    }

    [Fact]
    public void ClipboardPrivacyProcessingResult_UntrustedCount_IsDerivedFromCandidateMinusTrusted()
    {
        Assert.Equal(0, new ClipboardPrivacyProcessingResult(0, 0, 0, 0, 0).UntrustedCount);
        Assert.Equal(3, new ClipboardPrivacyProcessingResult(3, 0, 3, 0, 0).UntrustedCount);
        Assert.Equal(2, new ClipboardPrivacyProcessingResult(5, 3, 1, 1, 3).UntrustedCount);
        Assert.Equal(0, new ClipboardPrivacyProcessingResult(5, 5, 0, 0, 5).UntrustedCount);
    }

    // ---- structurally cannot carry raw/canonical/snapshot/evaluated-candidate/decision content
    // -- no property of any of the forbidden types exists at all, regardless of what any future
    // ToString override might do ----
    [Fact]
    public void ClipboardPrivacyProcessingResult_HasNoSensitiveTypedProperty()
    {
        var properties = typeof(ClipboardPrivacyProcessingResult).GetProperties();

        var forbiddenTypes = new[]
        {
            typeof(string), typeof(ClipboardTextSnapshot), typeof(DetectionResult),
            typeof(DetectionCandidate), typeof(CanonicalValue), typeof(TrustExceptionSnapshot),
            typeof(TrustedPublicValue), typeof(AmbiguousExceptionValue),
            typeof(IReadOnlyList<EvaluatedCandidate>), typeof(IReadOnlyList<CandidatePolicyDecision>),
        };

        Assert.DoesNotContain(properties, p => forbiddenTypes.Contains(p.PropertyType));
    }

    [Fact]
    public void ClipboardPrivacyProcessingResult_HasNoDebuggerDisplayOrTypeProxyAttributes()
    {
        var attributes = typeof(ClipboardPrivacyProcessingResult).GetCustomAttributes(inherit: false)
            .Select(a => a.GetType().Name);

        Assert.DoesNotContain(attributes, name => name.Contains("DebuggerDisplay") || name.Contains("DebuggerTypeProxy"));
    }

    // ==================================================================
    // ALIAS ASSIGNMENT WIRING / ALIAS_MAP_LIFETIME_FOR_CLIPBOARD
    // (Phase 3B STEP12, section 22 items 2-13)
    //
    // All tests below use ProcessWithAssignments (the internal test seam) purely to observe
    // real AliasAssigner/AliasMap wiring -- production Process() callers (the coordinator) never
    // see this seam or an AliasAssignment. Detailed alias-label/counter/disposition correctness
    // is Detection.Tests' own job (AliasFoundationTests) -- these only prove App's wiring and the
    // PER_PROCESS_ATTEMPT lifetime are genuine.
    // ==================================================================

    // ---- 2. one real Protect candidate -> one non-null alias assignment ----
    [Fact]
    public void ProcessWithAssignments_ProtectCandidate_GetsNonNullAlias()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider()); // untrusted -> Level2 Protect

        var (result, assignments) = processor.ProcessWithAssignments(ChatGpt, Snapshot("010-1234-5678"));

        Assert.Equal(1, result.ProtectCount);
        var assignment = Assert.Single(assignments!);
        Assert.Equal(CandidateDisposition.Protect, assignment.Decision.Disposition);
        Assert.NotNull(assignment.Alias);
        Assert.Equal("[전화번호1]", assignment.Alias!.Value.Value);
    }

    // ---- 3. one NeedsDecision candidate -> Alias == null ----
    [Fact]
    public void ProcessWithAssignments_NeedsDecisionCandidate_HasNullAlias()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        // Synthetic, structurally-plausible RRN (Level3 -> always NeedsDecision) -- not a real
        // person's number.
        var (result, assignments) = processor.ProcessWithAssignments(ChatGpt, Snapshot("901231-1234567"));

        Assert.Equal(1, result.NeedsDecisionCount);
        var assignment = Assert.Single(assignments!);
        Assert.Equal(CandidateDisposition.NeedsDecision, assignment.Decision.Disposition);
        Assert.Null(assignment.Alias);
    }

    // ---- 4. one Bypass candidate -> Alias == null ----
    [Fact]
    public void ProcessWithAssignments_BypassCandidate_HasNullAlias()
    {
        const string text = "37.5665,55.0000"; // order-ambiguous bare GPS pair -> Level1/Low -> Bypass when untrusted
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var (result, assignments) = processor.ProcessWithAssignments(ChatGpt, Snapshot(text));

        Assert.Equal(1, result.BypassCount);
        var assignment = Assert.Single(assignments!);
        Assert.Equal(CandidateDisposition.Bypass, assignment.Decision.Disposition);
        Assert.Null(assignment.Alias);
    }

    // ---- 5. same canonical PII repeated within one Process() -> same AliasToken (real
    // AliasMap.GetOrAdd identity, not manually deduplicated by App) ----
    [Fact]
    public void ProcessWithAssignments_SameCanonicalRepeatedWithinOneAttempt_ReusesSameAlias()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var (result, assignments) = processor.ProcessWithAssignments(
            ChatGpt, Snapshot("010-1234-5678 그리고 다시 010-1234-5678"));

        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(2, result.ProtectCount);
        Assert.All(assignments!, a => Assert.Equal("[전화번호1]", a.Alias!.Value.Value));
    }

    // ---- 6. two distinct values, same PiiType, within one Process() -> #1/#2 in raw appearance
    // order ----
    [Fact]
    public void ProcessWithAssignments_TwoDistinctValuesSamePiiType_NumberedSequentially()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var (_, assignments) = processor.ProcessWithAssignments(
            ChatGpt, Snapshot("A: 010-1234-5678, B: 010-9999-8888"));

        Assert.Equal(2, assignments!.Count);
        Assert.Equal("[전화번호1]", assignments[0].Alias!.Value.Value);
        Assert.Equal("[전화번호2]", assignments[1].Alias!.Value.Value);
    }

    // ---- 7. two different PiiTypes -> independent per-type counters ----
    [Fact]
    public void ProcessWithAssignments_TwoDifferentPiiTypes_IndependentCounters()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var (_, assignments) = processor.ProcessWithAssignments(
            ChatGpt, Snapshot("연락처 010-1234-5678, 이메일 user@company.test"));

        Assert.Equal(2, assignments!.Count);
        Assert.Contains(assignments, a => a.Alias!.Value.Value == "[전화번호1]");
        Assert.Contains(assignments, a => a.Alias!.Value.Value == "[이메일1]");
    }

    // ---- 8. second, separate Process() call -> fresh AliasMap -> numbering restarts from #1
    // (ALIAS_MAP_LIFETIME_FOR_CLIPBOARD == PER_PROCESS_ATTEMPT, Phase 3B STEP11) ----
    [Fact]
    public void ProcessWithAssignments_SecondSeparateCall_NumberingRestartsFromOne()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var (_, first) = processor.ProcessWithAssignments(ChatGpt, Snapshot("010-1234-5678"));
        var (_, second) = processor.ProcessWithAssignments(ChatGpt, Snapshot("010-9999-8888"));

        Assert.Equal("[전화번호1]", Assert.Single(first!).Alias!.Value.Value);
        // A DIFFERENT phone number on the second, separate call still gets number 1 -- proving
        // the AliasMap from the first call was not reused/retained.
        Assert.Equal("[전화번호1]", Assert.Single(second!).Alias!.Value.Value);
    }

    // ---- 9/10. assignment count equals decision count (== CandidateCount), and assignment
    // order matches raw text appearance order (AliasAssigner's own output-order contract) ----
    [Fact]
    public void ProcessWithAssignments_CountMatchesCandidateCount_AndOrderMatchesRawAppearance()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var (result, assignments) = processor.ProcessWithAssignments(
            ChatGpt, Snapshot("A: 010-1234-5678, B: 010-9999-8888"));

        Assert.Equal(result.CandidateCount, assignments!.Count);
        Assert.True(
            assignments[0].Decision.Candidate.Candidate.Span.Start < assignments[1].Decision.Candidate.Candidate.Span.Start);
    }

    // ---- 11/12. AliasMap and the AliasAssignment list are never processor fields (reflection --
    // see ClipboardPrivacyProcessor_HasNoRawOrSensitiveInstanceField above, which already
    // includes AliasMap/IReadOnlyList<AliasAssignment>/AliasToken in its forbidden-type list;
    // this is a focused, self-contained restatement for this STEP's own record) ----
    [Fact]
    public void ClipboardPrivacyProcessor_HasNoAliasMapOrAliasAssignmentField()
    {
        var fields = typeof(ClipboardPrivacyProcessor)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(AliasMap));
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(IReadOnlyList<AliasAssignment>));
    }

    // ---- 13. canonical synthetic sentinel does not appear via AliasMap's own diagnostic
    // surface (default object.ToString() -- type name only, no custom override to leak
    // anything) ----
    [Fact]
    public void AliasMap_ToString_DoesNotContainSentinel()
    {
        const string sentinel = "ALIAS-MAP-SENTINEL-902314";
        var map = new AliasMap();
        map.GetOrAdd(new CanonicalValue(PiiType.Phone, sentinel));

        Assert.DoesNotContain(sentinel, map.ToString());
    }

    // ==================================================================
    // ACCESSIBILITY (Phase 3B STEP4.1 -- APP_DETECTION_METADATA_RESULT correction)
    // ==================================================================

    // ---- the entire Detection-handoff and trust/exception seam is App-internal orchestration
    // state with no legitimate external consumer -- none of it widens Privon.App's public
    // surface ----
    [Theory]
    [InlineData(typeof(ClipboardPrivacyProcessingResult))]
    [InlineData(typeof(IClipboardPrivacyProcessor))]
    [InlineData(typeof(ClipboardPrivacyProcessor))]
    [InlineData(typeof(ClipboardPrivacyProcessingOutcome))]
    [InlineData(typeof(ClipboardWritePlan))]
    [InlineData(typeof(ClipboardDecisionPlan))]
    [InlineData(typeof(ClipboardDecisionItem))]
    public void DetectionHandoffTypes_AreNotPublic(Type type)
    {
        Assert.False(type.IsPublic);
    }

    // ==================================================================
    // ALIAS REPLACEMENT / WRITE PLAN (Phase 3B STEP14)
    //
    // NEEDSDECISION_BLOCKS_REPLACEMENT / ALL_BYPASS gating for the no-plan cases is already
    // proven, per fixture, by the trust/exception tests above (each already asserts
    // outcome.WritePlan is null in its own NeedsDecision/all-Bypass case). This region covers the
    // remaining focused cases: mixed Protect+Bypass, repeated/distinct canonical numbering
    // reflected in the actual rewritten text, and the write-plan diagnostic surface.
    // ==================================================================

    // ---- 6. Protect + Bypass -> only the Protect value is replaced, the Bypass value remains
    // raw in the rewritten text ----
    [Fact]
    public void Process_ProtectPlusBypass_OnlyProtectValueReplaced()
    {
        const string bypassGps = "37.5665,55.0000"; // order-ambiguous bare pair -> Level1/Low -> Bypass when untrusted
        const string protectPhone = "010-1234-5678"; // Level2 + Untrusted -> Protect
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot($"{bypassGps} {protectPhone}"));
        var result = outcome.Result;

        Assert.Equal(1, result.ProtectCount);
        Assert.Equal(1, result.BypassCount);
        Assert.Equal(0, result.NeedsDecisionCount);
        Assert.NotNull(outcome.WritePlan);
        Assert.Contains("[전화번호1]", outcome.WritePlan!.ReplacementText);
        Assert.DoesNotContain(protectPhone, outcome.WritePlan.ReplacementText);
        // The Bypass span is left exactly as raw text.
        Assert.Contains(bypassGps, outcome.WritePlan.ReplacementText);
    }

    // ---- 7. same canonical PII repeated -> the SAME alias token appears at both occurrences in
    // the actual rewritten text (not merely in the AliasAssignment list) ----
    [Fact]
    public void Process_RepeatedSameProtectCanonical_ReplacementTextUsesSameAliasConsistently()
    {
        const string phone = "010-1234-5678";
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot($"{phone} 그리고 다시 {phone}"));

        Assert.Equal(2, outcome.Result.ProtectCount);
        Assert.NotNull(outcome.WritePlan);
        var text = outcome.WritePlan!.ReplacementText;
        Assert.DoesNotContain(phone, text);
        Assert.Equal(2, text.Split("[전화번호1]").Length - 1);
    }

    // ---- 8. two distinct Protect values, same PiiType -> #1/#2 in the actual rewritten text ----
    [Fact]
    public void Process_DistinctProtectValuesSameType_ReplacementTextNumberedSequentially()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot("A: 010-1234-5678, B: 010-9999-8888"));

        Assert.Equal(2, outcome.Result.ProtectCount);
        Assert.NotNull(outcome.WritePlan);
        var text = outcome.WritePlan!.ReplacementText;
        Assert.Contains("[전화번호1]", text);
        Assert.Contains("[전화번호2]", text);
        Assert.DoesNotContain("010-1234-5678", text);
        Assert.DoesNotContain("010-9999-8888", text);
    }

    // ---- 23. write-plan ToString does not expose the replacement text ----
    [Fact]
    public void ClipboardWritePlan_ToString_DoesNotContainReplacementText()
    {
        const string sentinel = "RAW-WRITE-PLAN-SENTINEL-663012";
        var plan = new ClipboardWritePlan(sentinel);

        Assert.DoesNotContain(sentinel, plan.ToString());
        Assert.DoesNotContain(sentinel, $"{plan}");
    }

    [Fact]
    public void ClipboardWritePlan_HasNoDebuggerDisplayOrTypeProxyAttributes()
    {
        var attributes = typeof(ClipboardWritePlan).GetCustomAttributes(inherit: false).Select(a => a.GetType().Name);

        Assert.DoesNotContain(attributes, name => name.Contains("DebuggerDisplay") || name.Contains("DebuggerTypeProxy"));
    }

    // ==================================================================
    // DECISION PLAN (Phase 3B STEP19)
    //
    // NEEDSDECISION_BLOCKS_REPLACEMENT (no plan for no-PII/all-Bypass, WritePlan-only for
    // Protect-eligible) is already proven above -- this region focuses specifically on
    // DecisionPlan's own presence/absence, exact contents, deduplication, and diagnostics using
    // the REAL chain (fabricated ClipboardDecisionItem/ClipboardDecisionPlan construction is used
    // only for the two focused unit tests -- item structural-equality and plan defensive-copy --
    // that section 15's own instruction says must NOT be forced through a real-chain fixture).
    // ==================================================================

    // ---- 1. no PII -> both plans null ----
    [Fact]
    public void Process_NoPii_BothPlansNull()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot("오늘 날씨가 좋네요"));

        Assert.Null(outcome.WritePlan);
        Assert.Null(outcome.DecisionPlan);
    }

    // ---- 2. all Bypass -> both plans null ----
    [Fact]
    public void Process_AllBypass_BothPlansNull()
    {
        const string text = "37.5665,55.0000"; // order-ambiguous bare pair -> Level1/Low -> Bypass when untrusted
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, outcome.Result.BypassCount);
        Assert.Null(outcome.WritePlan);
        Assert.Null(outcome.DecisionPlan);
    }

    // ---- 3. Level1 NeedsDecision -> DecisionPlan present, WritePlan null, item RiskLevel Level1 ----
    [Fact]
    public void Process_Level1NeedsDecision_DecisionPlanPresent_ItemIsLevel1()
    {
        const string text = "37.5665,126.9780"; // order-unambiguous bare pair -> Level1/Medium -> NeedsDecision when untrusted
        var canonical = DiscoverCanonical(text, PiiType.GpsCoordinate, RiskLevel.Level1, DetectionConfidence.Medium);
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, outcome.Result.NeedsDecisionCount);
        Assert.Null(outcome.WritePlan);
        Assert.NotNull(outcome.DecisionPlan);
        var item = Assert.Single(outcome.DecisionPlan!.Items);
        Assert.Equal(RiskLevel.Level1, item.RiskLevel);
        Assert.Equal(canonical, item.Canonical);
    }

    // ---- 4. Level3 NeedsDecision -> DecisionPlan present, WritePlan null, item RiskLevel Level3 ----
    [Fact]
    public void Process_Level3NeedsDecision_DecisionPlanPresent_ItemIsLevel3()
    {
        // Synthetic, structurally-plausible RRN -- not a real person's number.
        const string text = "901231-1234567"; // Level3 -> always NeedsDecision
        var canonical = DiscoverCanonical(text, PiiType.ResidentRegistrationNumber, RiskLevel.Level3);
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, outcome.Result.NeedsDecisionCount);
        Assert.Null(outcome.WritePlan);
        Assert.NotNull(outcome.DecisionPlan);
        var item = Assert.Single(outcome.DecisionPlan!.Items);
        Assert.Equal(RiskLevel.Level3, item.RiskLevel);
        Assert.Equal(canonical, item.Canonical);
    }

    // ---- 5. Protect-only -> WritePlan present, DecisionPlan null ----
    [Fact]
    public void Process_ProtectOnly_WritePlanPresent_DecisionPlanNull()
    {
        const string text = "010-1234-5678";
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, outcome.Result.ProtectCount);
        Assert.NotNull(outcome.WritePlan);
        Assert.Null(outcome.DecisionPlan);
    }

    // ---- 6. Protect + Bypass -> WritePlan present, DecisionPlan null ----
    [Fact]
    public void Process_ProtectPlusBypass_WritePlanPresent_DecisionPlanNull()
    {
        const string bypassGps = "37.5665,55.0000";
        const string protectPhone = "010-1234-5678";
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot($"{bypassGps} {protectPhone}"));

        Assert.Equal(1, outcome.Result.ProtectCount);
        Assert.Equal(1, outcome.Result.BypassCount);
        Assert.NotNull(outcome.WritePlan);
        Assert.Null(outcome.DecisionPlan);
    }

    // ---- 7. Protect + NeedsDecision -> DecisionPlan present, WritePlan null, replacement still
    // blocked (NEEDSDECISION_BLOCKS_REPLACEMENT applies even with a Protect candidate present in
    // the same attempt) ----
    [Fact]
    public void Process_ProtectPlusNeedsDecision_DecisionPlanPresent_ReplacementStillBlocked()
    {
        const string protectPhone = "010-1234-5678"; // Level2 + Untrusted -> Protect
        const string needsDecisionRrn = "901231-1234567"; // Level3 -> always NeedsDecision
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot($"{protectPhone} {needsDecisionRrn}"));

        Assert.Equal(1, outcome.Result.ProtectCount);
        Assert.Equal(1, outcome.Result.NeedsDecisionCount);
        Assert.Null(outcome.WritePlan);
        Assert.NotNull(outcome.DecisionPlan);
        var item = Assert.Single(outcome.DecisionPlan!.Items);
        Assert.Equal(RiskLevel.Level3, item.RiskLevel);
    }

    // ---- 8. multiple distinct NeedsDecision candidates (mixed Level1 + Level3) -> both required
    // distinct identities present ----
    [Fact]
    public void Process_MixedLevel1AndLevel3NeedsDecision_BothItemsPresent()
    {
        const string level1Gps = "37.5665,126.9780"; // Level1/Medium NeedsDecision
        const string level3Rrn = "901231-1234567"; // Level3 NeedsDecision
        var level1Canonical = DiscoverCanonical(level1Gps, PiiType.GpsCoordinate, RiskLevel.Level1, DetectionConfidence.Medium);
        var level3Canonical = DiscoverCanonical(level3Rrn, PiiType.ResidentRegistrationNumber, RiskLevel.Level3);
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot($"{level1Gps} {level3Rrn}"));

        Assert.Equal(2, outcome.Result.NeedsDecisionCount);
        Assert.NotNull(outcome.DecisionPlan);
        Assert.Equal(2, outcome.DecisionPlan!.Items.Count);
        Assert.Contains(outcome.DecisionPlan.Items, i => i.RiskLevel == RiskLevel.Level1 && i.Canonical.Equals(level1Canonical));
        Assert.Contains(outcome.DecisionPlan.Items, i => i.RiskLevel == RiskLevel.Level3 && i.Canonical.Equals(level3Canonical));
    }

    // ---- 9. duplicate same CanonicalValue + RiskLevel -> collapsed to exactly one item, even
    // though NeedsDecisionCount itself still reflects both raw occurrences ----
    [Fact]
    public void Process_DuplicateNeedsDecisionCanonical_CollapsedToOneItem()
    {
        const string rrn = "901231-1234567";
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot($"{rrn} 그리고 다시 {rrn}"));

        Assert.Equal(2, outcome.Result.NeedsDecisionCount);
        Assert.NotNull(outcome.DecisionPlan);
        Assert.Single(outcome.DecisionPlan!.Items);
    }

    // ---- 10. two distinct NeedsDecision candidates -> plan order matches raw appearance order ----
    [Fact]
    public void Process_TwoDistinctLevel1NeedsDecisionCandidates_OrderMatchesRawAppearance()
    {
        const string first = "37.5665,126.9780";
        const string second = "35.1796,129.0756";
        var firstCanonical = DiscoverCanonical(first, PiiType.GpsCoordinate, RiskLevel.Level1, DetectionConfidence.Medium);
        var secondCanonical = DiscoverCanonical(second, PiiType.GpsCoordinate, RiskLevel.Level1, DetectionConfidence.Medium);
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot($"A: {first}, B: {second}"));

        Assert.Equal(2, outcome.Result.NeedsDecisionCount);
        Assert.NotNull(outcome.DecisionPlan);
        Assert.Equal(2, outcome.DecisionPlan!.Items.Count);
        Assert.Equal(firstCanonical, outcome.DecisionPlan.Items[0].Canonical);
        Assert.Equal(secondCanonical, outcome.DecisionPlan.Items[1].Canonical);
    }

    // ---- 11. same CanonicalValue but a DIFFERENT RiskLevel are distinct identities for
    // deduplication -- a focused item-level unit test (Phase 3B STEP18 §15: do not force an
    // artificial real-chain fixture for a state current detectors cannot actually produce) ----
    [Fact]
    public void ClipboardDecisionItem_SameCanonicalDifferentRiskLevel_AreDistinctForDeduplication()
    {
        var canonical = new CanonicalValue(PiiType.Phone, "01012345678");
        var item1 = new ClipboardDecisionItem(canonical, RiskLevel.Level1);
        var item2 = new ClipboardDecisionItem(canonical, RiskLevel.Level2);

        Assert.NotEqual(item1, item2);
        var set = new HashSet<ClipboardDecisionItem> { item1, item2 };
        Assert.Equal(2, set.Count);
    }

    // ---- 12. ClipboardDecisionPlan defensively materializes its input -- later mutation of the
    // caller's original collection never alters the plan's own contents ----
    [Fact]
    public void ClipboardDecisionPlan_DefensivelyMaterializesInputCollection()
    {
        var canonical = new CanonicalValue(PiiType.Phone, "01012345678");
        var mutableList = new List<ClipboardDecisionItem> { new(canonical, RiskLevel.Level1) };
        var plan = new ClipboardDecisionPlan(mutableList);

        mutableList.Add(new ClipboardDecisionItem(canonical, RiskLevel.Level2));
        mutableList.Clear();

        Assert.Single(plan.Items);
    }

    // ---- 13. ClipboardDecisionItem diagnostic surface never exposes the canonical value ----
    [Fact]
    public void ClipboardDecisionItem_ToString_DoesNotContainCanonicalValueOrRawSentinel()
    {
        const string sentinel = "RAW-DECISION-ITEM-SENTINEL-204817";
        var item = new ClipboardDecisionItem(new CanonicalValue(PiiType.Phone, sentinel), RiskLevel.Level1);

        Assert.DoesNotContain(sentinel, item.ToString());
        Assert.DoesNotContain(sentinel, $"{item}");
        Assert.Contains("PiiType", item.ToString());
        Assert.Contains("RiskLevel", item.ToString());
    }

    // ---- 14. ClipboardDecisionPlan diagnostic surface never exposes item contents, only a count ----
    [Fact]
    public void ClipboardDecisionPlan_ToString_DoesNotContainCanonicalValueOrRawSentinel()
    {
        const string sentinel = "RAW-DECISION-PLAN-SENTINEL-204817";
        var plan = new ClipboardDecisionPlan([new ClipboardDecisionItem(new CanonicalValue(PiiType.Phone, sentinel), RiskLevel.Level1)]);

        Assert.DoesNotContain(sentinel, plan.ToString());
        Assert.DoesNotContain(sentinel, $"{plan}");
        Assert.Contains("ItemCount", plan.ToString());
    }

    // ---- 15. real NeedsDecision sentinel never surfaces through the outcome's own diagnostics
    // (nor the DecisionPlan it carries) ----
    [Fact]
    public void Process_NeedsDecisionRealSentinelText_NeverAppearsInOutcomeDiagnostics()
    {
        const string sentinel = "RAW-APP-DECISION-SENTINEL-771539";
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot($"{sentinel} 901231-1234567"));

        Assert.NotNull(outcome.DecisionPlan);
        Assert.DoesNotContain(sentinel, outcome.ToString());
        Assert.DoesNotContain(sentinel, $"{outcome}");
        Assert.DoesNotContain(sentinel, outcome.DecisionPlan!.ToString());
    }

    // ---- 16. no DebuggerDisplay/DebuggerTypeProxy leak on any of the new/extended types ----
    [Theory]
    [InlineData(typeof(ClipboardDecisionItem))]
    [InlineData(typeof(ClipboardDecisionPlan))]
    [InlineData(typeof(ClipboardPrivacyProcessingOutcome))]
    public void DecisionPlanTypes_HaveNoDebuggerDisplayOrTypeProxyAttributes(Type type)
    {
        var attributes = type.GetCustomAttributes(inherit: false).Select(a => a.GetType().Name);

        Assert.DoesNotContain(attributes, name => name.Contains("DebuggerDisplay") || name.Contains("DebuggerTypeProxy"));
    }
}
