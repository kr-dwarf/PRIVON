using System.Diagnostics;
using Privon.Detection;

namespace Privon.Detection.Tests;

// Phase 0.2G -- Immediate Paste Race Gate: AUTOMATED_EVIDENCE for the pure-CPU (no OS clipboard
// I/O, no real Windows calls) portion of the "evaluation claimed -> clipboard verified-protected"
// latency window -- the real Detect -> Trust/Exception -> Base Policy -> Alias Assignment ->
// Alias Replacement chain, using the exact same public API sequence
// Privon.App.ClipboardPrivacyProcessor.ProcessWithAssignments uses internally (see that type's
// own doc). This complements the separate, real-Win32, real-system-clipboard measurement done by
// the standalone tools/ClipboardRaceMeasurement tool (NOT part of this regression suite, since it
// mutates the real OS clipboard -- see that tool's own header comment) -- this file exists so the
// CPU-bound component of the race window has a deterministic, CI-safe, permanently-checked-in
// regression, independent of any particular machine's clipboard I/O characteristics.
//
// These are DIAGNOSTIC latency assertions, not correctness assertions -- the ceiling values below
// are deliberately generous (two to three orders of magnitude above every real measurement taken
// during the Phase 0.2G session on real hardware) specifically to avoid becoming a source of
// flaky CI failures under unrelated load; they exist only to catch a genuine future regression
// (e.g. an accidentally-quadratic detector change), not to assert a tight SLA.
public class Phase0_2G_ClipboardRaceLatencyRegressionTests
{
    private static readonly IReadOnlyList<AmbiguousExceptionValue> NoExceptions = [];
    private static readonly IReadOnlyList<TrustedPublicValue> NoTrusted = [];

    private static TimeSpan MeasureFullChain(string rawText)
    {
        var pipeline = DetectionPipeline.CreateDefault();

        // Warm up once outside the measured window -- JIT/regex-compilation cost is a one-time,
        // process-lifetime cost in the real ClipboardPrivacyProcessor (constructed once at App
        // startup, reused for every subsequent clipboard attempt), never repeated per-attempt.
        // The Phase 0.2G tool's own real measurement observed this exact effect (~190ms on trial
        // 0 only, sub-millisecond on every subsequent trial).
        _ = pipeline.Detect(rawText);

        var sw = Stopwatch.StartNew();
        var detection = pipeline.Detect(rawText);
        if (detection.Candidates.Count > 0)
        {
            var evaluated = ExceptionTrustedEvaluator.Evaluate(detection, NoExceptions, NoTrusted);
            var decisions = CandidatePolicyEvaluator.Evaluate(evaluated);
            if (decisions.Any(d => d.Disposition == CandidateDisposition.Protect))
            {
                var assignments = AliasAssigner.Assign(decisions, new AliasMap());
                _ = AliasReplacer.Apply(rawText, assignments);
            }
        }
        sw.Stop();
        return sw.Elapsed;
    }

    [Theory]
    [InlineData("문의 전화 010-1234-5678")]
    [InlineData("제 이메일은 synthetic.test.user@example.com 입니다")]
    [InlineData("연락처: 010-9876-5432, 이메일: fake.contact@example.org, 참고 부탁드립니다.")]
    public void FullChain_ShortSyntheticClipboardText_CompletesWellUnderGenerousCeiling(string rawText)
    {
        var elapsed = MeasureFullChain(rawText);

        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(200),
            $"Detect->Trust->Policy->Alias chain took {elapsed.TotalMilliseconds:F3}ms for a short " +
            "synthetic clipboard string -- this is far above every real measurement taken on real " +
            "hardware during the Phase 0.2G session (sub-millisecond, post-warmup) and may indicate " +
            "a genuine performance regression in the detection/policy/alias chain.");
    }

    // A realistic "worst-case-length" synthetic paste: several PII types repeated across a
    // multi-line paragraph, closer to what a user might actually copy from a form or document
    // than a single short phrase.
    private const string RealisticParagraph =
        """
        안녕하세요, 문의드립니다.
        이름: 홍길동 (테스트용 가상 인물)
        연락처: 010-2345-6789 / 보조 연락처: 010-8765-4321
        이메일: synthetic.paragraph.user@example.com
        회사 이메일: fake.corporate.contact@example.org
        참고로 예전 연락처는 010-1111-2222 였습니다.
        빠른 회신 부탁드립니다. 감사합니다.
        """;

    [Fact]
    public void FullChain_RealisticMultiLineParagraph_CompletesWellUnderGenerousCeiling()
    {
        var elapsed = MeasureFullChain(RealisticParagraph);

        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(200),
            $"Detect->Trust->Policy->Alias chain took {elapsed.TotalMilliseconds:F3}ms for a " +
            "realistic multi-line synthetic paragraph -- far above every real measurement taken " +
            "on real hardware during the Phase 0.2G session.");
    }

    // A very large synthetic paste (repeated realistic paragraph, ~50x) -- an intentionally
    // extreme upper bound on clipboard content size, to confirm the chain does not degrade
    // catastrophically (e.g. quadratically) as input size grows, independent of any specific
    // millisecond ceiling.
    [Fact]
    public void FullChain_VeryLargeSyntheticText_DoesNotDegradeCatastrophicallyWithSize()
    {
        var large = string.Join('\n', Enumerable.Repeat(RealisticParagraph, 50));

        var smallElapsed = MeasureFullChain(RealisticParagraph);
        var largeElapsed = MeasureFullChain(large);

        // ~50x the input size should not cost more than ~500x the time (generous headroom well
        // above linear scaling) -- this is a coarse anti-quadratic-blowup guard, not a tight
        // performance assertion.
        var ceiling = TimeSpan.FromTicks(Math.Max(smallElapsed.Ticks * 500, TimeSpan.FromMilliseconds(500).Ticks));
        Assert.True(
            largeElapsed < ceiling,
            $"A ~50x larger synthetic input took {largeElapsed.TotalMilliseconds:F3}ms " +
            $"(small-input baseline was {smallElapsed.TotalMilliseconds:F3}ms, ceiling was " +
            $"{ceiling.TotalMilliseconds:F3}ms) -- this suggests worse-than-expected scaling in the " +
            "detection/policy/alias chain as clipboard content size grows.");
    }

    // Empirically confirms, at the pure-Detection layer (independent of any App/Windows wiring),
    // the Phase 0.2G finding that RiskLevel.Level3 content (RRN/Card/BankAccount/Secret) always
    // yields CandidateDisposition.NeedsDecision -- i.e. the real ClipboardPrivacyProcessor would
    // never produce a WritePlan for this text, so no automatic clipboard rewrite -- and therefore
    // no bounded latency number -- exists for this category at all. See the Phase 0.2G session
    // report's NeedsDecision findings for the full analysis.
    [Fact]
    public void Level3Content_NeverEndsInWritePlan_ExposureIsUnboundedNotLatencyBound()
    {
        const string syntheticRrnText = "주민등록번호 900101-1234567 확인 부탁드립니다";

        var detection = DetectionPipeline.CreateDefault().Detect(syntheticRrnText);
        Assert.NotEmpty(detection.Candidates);

        var evaluated = ExceptionTrustedEvaluator.Evaluate(detection, NoExceptions, NoTrusted);
        var decisions = CandidatePolicyEvaluator.Evaluate(evaluated);

        Assert.Contains(decisions, d => d.Disposition == CandidateDisposition.NeedsDecision);
        // NEEDSDECISION_BLOCKS_REPLACEMENT: real ClipboardPrivacyProcessor never calls
        // AliasReplacer at all when any candidate is NeedsDecision -- this test does not call it
        // either, matching production exactly, and there is therefore no "replacement latency" to
        // measure for this scenario.
    }
}
