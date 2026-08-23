using System.Diagnostics;
using System.Globalization;
using Privon.Detection;
using Privon.Windows;

// PHASE 0.2G -- IMMEDIATE PASTE RACE GATE MEASUREMENT TOOL
//
// Standalone, non-solution-registered diagnostic tool (mirrors tools/UiaInspector's own "not in
// PRIVON.slnx" precedent). Measures REAL Windows-layer + REAL Detection-layer latency for the
// "evaluation claimed -> clipboard verified-protected" portion of PRIVON's clipboard protection
// pipeline, using ONLY the real, public production API surface of Privon.Windows and
// Privon.Detection (DetectionPipeline / ExceptionTrustedEvaluator / CandidatePolicyEvaluator /
// AliasAssigner / AliasReplacer / ClipboardChangeMonitor / ForegroundTargetInspector). No test
// doubles. No InternalsVisibleTo. No production source file is modified by this tool's existence.
//
// SCENARIO MODELED: "raw PII was already copied in another app; the user has just switched focus
// to ChatGPT; PRIVON's evaluation for the still-current clipboard generation has just been
// claimed." T0 below begins at exactly that point -- immediately before foreground target capture,
// mirroring ClipboardPrivacyCoordinator.ProcessWorkItemAsync -> RunPrivacyPipelineAsync's own real
// call order exactly (target capture -> guarded read -> Detect/Trust/Policy/Alias -> guarded
// write). T1 is the moment the guarded write call returns.
//
// WHAT THIS TOOL DOES NOT AND CANNOT MEASURE (see Phase 0.2G session report for why):
//   (a) The real OS EVENT_SYSTEM_FOREGROUND -> ForegroundChangeMonitor callback delivery latency
//       for an ACTUAL human-driven foreground-window switch. This tool never calls
//       SetForegroundWindow or drives ForegroundChangeMonitor at all -- it directly re-uses
//       whatever window already happens to be the real OS foreground window at the moment each
//       trial runs (ForegroundTargetInspector.Capture(), completely unmodified, completely real).
//       Simulating a synthetic focus SWITCH via SetForegroundWindow from a non-interactive/
//       automated context is documented elsewhere in this codebase as unreliable
//       (MANUAL_WINDOWS_FOREGROUND_CHANGE_SMOKE) -- this tool does not attempt it.
//   (b) Whether a real ChatGPT Desktop paste handler actually observes raw or protected clipboard
//       content for any given real human keystroke timing. ChatGPT Desktop is never launched,
//       driven, or assumed present by this tool.
// Both remain MANUAL_QA_REQUIRED -- see the Phase 0.2G report's MANUAL_QA_STEPS.
//
// SYNTHETIC DATA ONLY: every PII value this tool writes to the clipboard is fabricated
// (RFC 2606 / obviously-fake numbers). This tool never reads pre-existing clipboard content for
// any purpose other than learning a CAS sequence number to seed its own next write.

const int TrialCount = 320;

// Deliberately includes one RiskLevel.Level3 case ("900101-1234567", a synthetic,
// checksum-inconsistent RRN-shaped value -- never a real person's data). Level3 (RRN/Card/
// BankAccount/Secret) is ALWAYS NeedsDecision regardless of TrustState/Confidence
// (CandidatePolicyEvaluator.Decide) -- ClipboardPrivacyProcessor produces no WritePlan at all in
// that case (NEEDSDECISION_BLOCKS_REPLACEMENT), so the clipboard is never rewritten by this
// pipeline for that generation. This tool measures and reports that distinctly from the
// auto-protected Level1/Level2 cases below -- see the "NEEDS_DECISION" category in the summary.
string[] syntheticPiiTexts =
[
    "문의 전화 010-1234-5678",
    "제 이메일은 synthetic.test.user@example.com 입니다",
    "연락처: 010-9876-5432, 이메일: fake.contact@example.org, 참고 부탁드립니다.",
    "주민등록번호 900101-1234567 확인 부탁드립니다",
];

var results = new List<TrialOutcome>(TrialCount);
int skipped = 0;

var monitor = new ClipboardChangeMonitor();
monitor.Start();

Console.WriteLine("PRIVON Phase 0.2G -- Clipboard Race Measurement Tool");
Console.WriteLine($"Trials requested: {TrialCount}; synthetic PII variants: {syntheticPiiTexts.Length}");
Console.WriteLine("Measuring: foreground target capture -> guarded read -> Detect/Trust/Policy/Alias -> guarded write.");
Console.WriteLine();

var inspector = new ForegroundTargetInspector();
var pipeline = DetectionPipeline.CreateDefault();
var noExceptions = Array.Empty<AmbiguousExceptionValue>();
var noTrusted = Array.Empty<TrustedPublicValue>();

try
{
    for (int i = 0; i < TrialCount; i++)
    {
        string syntheticText = syntheticPiiTexts[i % syntheticPiiTexts.Length];

        // ---- SEED (untimed): simulate "another app already copied this PII, well before the
        // measured attempt begins" -- models the real attack scenario (copy elsewhere, THEN
        // switch focus to ChatGPT) rather than measuring the write that PUTS the PII there. ----
        var seedTarget = inspector.Capture();
        if (!seedTarget.IsResolved)
        {
            Console.WriteLine($"[trial {i,3}] SKIP -- no resolved foreground target available for seeding.");
            skipped++;
            continue;
        }

        var preRead = await monitor.ReadTextSnapshotAsync(seedTarget);
        if (preRead.Outcome != ClipboardReadOutcome.Success || preRead.Snapshot is null || !preRead.Snapshot.Value.HasReliableSequence)
        {
            Console.WriteLine($"[trial {i,3}] SKIP -- pre-seed read outcome={preRead.Outcome} (unavailable/unreliable sequence).");
            skipped++;
            continue;
        }

        var seedWrite = await monitor.WriteTextIfSequenceMatchesAsync(seedTarget, preRead.Snapshot.Value.SequenceNumber, syntheticText);
        if (seedWrite.Outcome != ClipboardWriteOutcome.Success || !seedWrite.ClipboardMutated)
        {
            Console.WriteLine($"[trial {i,3}] SKIP -- seed write outcome={seedWrite.Outcome} mutated={seedWrite.ClipboardMutated}.");
            skipped++;
            continue;
        }

        // ==== MEASURED WINDOW BEGINS ====
        // T0: "evaluation has just been claimed for a generation whose clipboard content is raw,
        // unprotected synthetic PII" -- the earliest point PRIVON's real pipeline would begin
        // acting once a foreground-triggered (or clipboard-triggered) attempt is authorized.
        var sw = Stopwatch.StartNew();

        var target = inspector.Capture();
        var targetCapturedAt = sw.Elapsed;

        if (!target.IsResolved)
        {
            sw.Stop();
            Console.WriteLine($"[trial {i,3}] SKIP -- foreground target lost between seed and measured attempt.");
            skipped++;
            continue;
        }

        var read = await monitor.ReadTextSnapshotAsync(target);
        var readCompletedAt = sw.Elapsed;

        if (read.Outcome != ClipboardReadOutcome.Success || read.Snapshot is null)
        {
            sw.Stop();
            results.Add(TrialOutcome.ReadFailed(i, sw.Elapsed, read.Outcome));
            Console.WriteLine($"[trial {i,3}] READ_FAILED outcome={read.Outcome} elapsed={sw.Elapsed.TotalMilliseconds:F3}ms");
            continue;
        }

        var snapshot = read.Snapshot.Value;

        // Real Detect -> Trust/Exception -> Base Policy -> Alias Assignment chain, mirroring
        // Privon.App.ClipboardPrivacyProcessor.ProcessWithAssignments's own exact branch logic
        // (NeedsDecisionCount > 0 blocks replacement entirely; ProtectCount == 0 means no write
        // plan at all; only NeedsDecisionCount == 0 && ProtectCount > 0 produces a rewritten
        // string via the real AliasReplacer).
        var detectionResult = pipeline.Detect(snapshot.Text);
        bool hasWritePlan;
        bool blockedByNeedsDecision = false;
        string replacementText = snapshot.Text;

        if (detectionResult.Candidates.Count > 0)
        {
            var evaluated = ExceptionTrustedEvaluator.Evaluate(detectionResult, noExceptions, noTrusted);
            var decisions = CandidatePolicyEvaluator.Evaluate(evaluated);

            bool needsDecision = false;
            bool anyProtect = false;
            foreach (var decision in decisions)
            {
                if (decision.Disposition == CandidateDisposition.NeedsDecision) needsDecision = true;
                if (decision.Disposition == CandidateDisposition.Protect) anyProtect = true;
            }

            if (needsDecision)
            {
                // NEEDSDECISION_BLOCKS_REPLACEMENT (frozen production policy): the real
                // ClipboardPrivacyProcessor produces NO WritePlan whatsoever here -- not even a
                // partial replacement of the OTHER protectable candidates in the same text. The
                // clipboard is left completely untouched, indefinitely, until a human resolves the
                // NeedsDecision prompt. This is the scenario this tool most needs to surface: for
                // RiskLevel.Level3 content (RRN/Card/BankAccount/Secret), automatic protection
                // latency is not the bottleneck at all -- the write never happens automatically.
                hasWritePlan = false;
                blockedByNeedsDecision = true;
            }
            else if (!anyProtect)
            {
                hasWritePlan = false;
            }
            else
            {
                var aliasMap = new AliasMap();
                var assignments = AliasAssigner.Assign(decisions, aliasMap);
                replacementText = AliasReplacer.Apply(snapshot.Text, assignments);
                hasWritePlan = true;
            }
        }
        else
        {
            hasWritePlan = false;
        }

        var processingCompletedAt = sw.Elapsed;

        if (blockedByNeedsDecision)
        {
            sw.Stop();

            // Empirically confirm the clipboard is genuinely still the ORIGINAL raw seeded text --
            // not merely "we didn't call write," but "the OS clipboard still contains exactly what
            // an attacker/other-app put there," proving the exposure is real, not theoretical.
            var confirmRead = await monitor.ReadTextSnapshotAsync(target);
            bool stillRaw = confirmRead.Outcome == ClipboardReadOutcome.Success
                && confirmRead.Snapshot is not null
                && confirmRead.Snapshot.Value.Text == syntheticText;

            results.Add(TrialOutcome.NeedsDecisionBlocked(i, sw.Elapsed, stillRaw));
            Console.WriteLine(
                $"[trial {i,3}] NEEDS_DECISION_BLOCKED elapsed={sw.Elapsed.TotalMilliseconds:F3}ms " +
                $"clipboard_still_raw={stillRaw} -- **UNBOUNDED EXPOSURE until a human resolves the prompt.**");
            continue;
        }

        if (!hasWritePlan)
        {
            sw.Stop();
            results.Add(TrialOutcome.NoActionRequired(i, sw.Elapsed));
            Console.WriteLine($"[trial {i,3}] NO_ACTION_REQUIRED (all-Bypass; nothing needed protecting) elapsed={sw.Elapsed.TotalMilliseconds:F3}ms");
            continue;
        }

        if (!snapshot.HasReliableSequence)
        {
            sw.Stop();
            results.Add(TrialOutcome.WriteSkippedUnreliableSequence(i, sw.Elapsed));
            Console.WriteLine($"[trial {i,3}] WRITE_SKIPPED_UNRELIABLE_SEQUENCE elapsed={sw.Elapsed.TotalMilliseconds:F3}ms -- clipboard REMAINS RAW/UNPROTECTED.");
            continue;
        }

        var write = await monitor.WriteTextIfSequenceMatchesAsync(target, snapshot.SequenceNumber, replacementText);
        sw.Stop();
        // ==== MEASURED WINDOW ENDS (T1) ====

        bool verified = write.Outcome == ClipboardWriteOutcome.Success && write.ClipboardMutated;

        var outcome = new TrialOutcome(
            i, sw.Elapsed, targetCapturedAt, readCompletedAt, processingCompletedAt,
            write.Outcome, write.ClipboardMutated, verified);
        results.Add(outcome);

        string tag = verified ? "VERIFIED" : "**UNPROTECTED-AT-END**";
        Console.WriteLine(
            $"[trial {i,3}] total={sw.Elapsed.TotalMilliseconds,7:F3}ms  " +
            $"target={targetCapturedAt.TotalMilliseconds,6:F3}ms  " +
            $"read={(readCompletedAt - targetCapturedAt).TotalMilliseconds,6:F3}ms  " +
            $"process={(processingCompletedAt - readCompletedAt).TotalMilliseconds,6:F3}ms  " +
            $"write={(sw.Elapsed - processingCompletedAt).TotalMilliseconds,6:F3}ms  " +
            $"outcome={write.Outcome,-12} mutated={write.ClipboardMutated,-5} {tag}");
    }
}
finally
{
    // Cleanup: never leave synthetic PII (raw or otherwise) sitting on the real clipboard after
    // this tool exits.
    try
    {
        var cleanupTarget = inspector.Capture();
        if (cleanupTarget.IsResolved)
        {
            var cleanupRead = await monitor.ReadTextSnapshotAsync(cleanupTarget);
            if (cleanupRead.Outcome == ClipboardReadOutcome.Success && cleanupRead.Snapshot is not null && cleanupRead.Snapshot.Value.HasReliableSequence)
            {
                await monitor.WriteTextIfSequenceMatchesAsync(
                    cleanupTarget, cleanupRead.Snapshot.Value.SequenceNumber,
                    "PRIVON Phase 0.2G measurement complete -- clipboard cleared of synthetic test data.");
            }
        }
    }
    catch
    {
        // Best-effort cleanup only -- never let a cleanup failure mask the measurement results
        // already collected above.
    }

    monitor.Stop();
    monitor.Dispose();
}

Console.WriteLine();
Console.WriteLine("=== SUMMARY ===");
Console.WriteLine($"Trials requested: {TrialCount}");
Console.WriteLine($"Trials skipped (seed/target unavailable, not counted as a measured attempt): {skipped}");
Console.WriteLine($"Trials measured: {results.Count}");

var byOutcome = results
    .GroupBy(r => r.WriteOutcome?.ToString() ?? r.Category)
    .OrderByDescending(g => g.Count());
Console.WriteLine();
Console.WriteLine("Outcome distribution:");
foreach (var group in byOutcome)
    Console.WriteLine($"  {group.Key,-40} {group.Count(),5}  ({100.0 * group.Count() / Math.Max(1, results.Count):F1}%)");

// Only trials that actually reached a guarded-write attempt (Category == "Write") represent the
// "evaluation claimed -> clipboard verified-protected" window this tool exists to measure.
// NeedsDecisionBlocked/NoActionRequired/ReadFailed/WriteSkippedUnreliableSequence trials never
// attempt a write at all -- mixing their (much smaller) elapsed times into "worst case" latency
// would UNDERSTATE the real exposure, not overstate it, so they are reported completely separately
// below rather than folded into these percentiles.
var writeAttempts = results.Where(r => r.Category == "Write").ToArray();
var verifiedDurations = writeAttempts.Where(r => r.Verified).Select(r => r.Total.TotalMilliseconds).OrderBy(x => x).ToArray();
var allWriteAttemptDurations = writeAttempts.Select(r => r.Total.TotalMilliseconds).OrderBy(x => x).ToArray();

Console.WriteLine();
Console.WriteLine($"VERIFIED (clipboard confirmed protected) write attempts: {verifiedDurations.Length} / {writeAttempts.Length}");
if (verifiedDurations.Length > 0)
{
    Console.WriteLine($"  p50  = {Percentile(verifiedDurations, 0.50):F3} ms");
    Console.WriteLine($"  p95  = {Percentile(verifiedDurations, 0.95):F3} ms");
    Console.WriteLine($"  p99  = {Percentile(verifiedDurations, 0.99):F3} ms");
    Console.WriteLine($"  mean = {verifiedDurations.Average():F3} ms");
    Console.WriteLine($"  max  = {verifiedDurations.Max():F3} ms");
    Console.WriteLine($"  min  = {verifiedDurations.Min():F3} ms");
}

Console.WriteLine();
Console.WriteLine($"ALL write attempts (regardless of final outcome), worst-case across the set:");
if (allWriteAttemptDurations.Length > 0)
{
    Console.WriteLine($"  p50  = {Percentile(allWriteAttemptDurations, 0.50):F3} ms");
    Console.WriteLine($"  p95  = {Percentile(allWriteAttemptDurations, 0.95):F3} ms");
    Console.WriteLine($"  p99  = {Percentile(allWriteAttemptDurations, 0.99):F3} ms");
    Console.WriteLine($"  max  = {allWriteAttemptDurations.Max():F3} ms");
}

int nonVerifiedWriteCount = writeAttempts.Count(r => !r.Verified);
Console.WriteLine();
Console.WriteLine($"NON-VERIFIED write attempts (clipboard left RAW/unprotected until the next trigger): {nonVerifiedWriteCount} / {writeAttempts.Length} ({100.0 * nonVerifiedWriteCount / Math.Max(1, writeAttempts.Length):F2}%)");

var needsDecisionTrials = results.Where(r => r.Category.StartsWith("NeedsDecisionBlocked", StringComparison.Ordinal)).ToArray();
int stillRawCount = needsDecisionTrials.Count(r => r.Category.Contains("StillRaw=True", StringComparison.Ordinal));
Console.WriteLine();
Console.WriteLine("*** NeedsDecision (RiskLevel.Level3 -- RRN/Card/BankAccount/Secret) trials ***");
Console.WriteLine($"  Trials that hit this path: {needsDecisionTrials.Length}");
Console.WriteLine($"  Confirmed clipboard still holds the ORIGINAL raw text after processing: {stillRawCount} / {needsDecisionTrials.Length}");
Console.WriteLine("  NOTE: the elapsed time recorded for these trials is ONLY how fast PRIVON gave up trying to");
Console.WriteLine("  auto-protect (a few hundred microseconds) -- it is NOT the real exposure duration. For this");
Console.WriteLine("  PII category, NO automatic clipboard write ever happens; the clipboard remains 100% raw and");
Console.WriteLine("  fully paste-able until a human manually resolves the NeedsDecision prompt (or the generation");
Console.WriteLine("  is superseded by a newer clipboard/foreground event) -- an UNBOUNDED exposure window, not a");
Console.WriteLine("  latency number at all.");

// Raw CSV dump for full transparency / independent re-analysis.
string csvPath = Path.Combine(AppContext.BaseDirectory, "phase-0-2g-latency-raw.csv");
await using (var csv = new StreamWriter(csvPath, append: false))
{
    await csv.WriteLineAsync("trial,category,total_ms,target_capture_ms,read_ms,process_ms,write_ms,write_outcome,clipboard_mutated,verified");
    foreach (var r in results)
    {
        double targetMs = r.TargetCapturedAt.TotalMilliseconds;
        double readMs = (r.ReadCompletedAt - r.TargetCapturedAt).TotalMilliseconds;
        double processMs = (r.ProcessingCompletedAt - r.ReadCompletedAt).TotalMilliseconds;
        double writeMs = (r.Total - r.ProcessingCompletedAt).TotalMilliseconds;
        await csv.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"{r.Index},{r.Category},{r.Total.TotalMilliseconds:F4},{targetMs:F4},{readMs:F4},{processMs:F4},{writeMs:F4},{r.WriteOutcome},{r.ClipboardMutated},{r.Verified}"));
    }
}
Console.WriteLine();
Console.WriteLine($"Raw per-trial CSV written to: {csvPath}");

static double Percentile(double[] sortedAscending, double p)
{
    if (sortedAscending.Length == 0) return double.NaN;
    if (sortedAscending.Length == 1) return sortedAscending[0];
    double rank = p * (sortedAscending.Length - 1);
    int lo = (int)Math.Floor(rank);
    int hi = (int)Math.Ceiling(rank);
    if (lo == hi) return sortedAscending[lo];
    double frac = rank - lo;
    return sortedAscending[lo] + (sortedAscending[hi] - sortedAscending[lo]) * frac;
}

internal readonly record struct TrialOutcome
{
    public int Index { get; }
    public string Category { get; }
    public TimeSpan Total { get; }
    public TimeSpan TargetCapturedAt { get; }
    public TimeSpan ReadCompletedAt { get; }
    public TimeSpan ProcessingCompletedAt { get; }
    public ClipboardWriteOutcome? WriteOutcome { get; }
    public bool ClipboardMutated { get; }
    public bool Verified { get; }

    public TrialOutcome(
        int index, TimeSpan total, TimeSpan targetCapturedAt, TimeSpan readCompletedAt,
        TimeSpan processingCompletedAt, ClipboardWriteOutcome writeOutcome, bool clipboardMutated, bool verified)
    {
        Index = index;
        Category = "Write";
        Total = total;
        TargetCapturedAt = targetCapturedAt;
        ReadCompletedAt = readCompletedAt;
        ProcessingCompletedAt = processingCompletedAt;
        WriteOutcome = writeOutcome;
        ClipboardMutated = clipboardMutated;
        Verified = verified;
    }

    private TrialOutcome(int index, string category, TimeSpan total, ClipboardReadOutcome? readOutcome)
    {
        Index = index;
        Category = readOutcome is null ? category : $"{category}:{readOutcome}";
        Total = total;
        TargetCapturedAt = default;
        ReadCompletedAt = default;
        ProcessingCompletedAt = default;
        WriteOutcome = null;
        ClipboardMutated = false;
        Verified = false;
    }

    public static TrialOutcome ReadFailed(int index, TimeSpan total, ClipboardReadOutcome readOutcome) =>
        new(index, "ReadFailed", total, readOutcome);

    public static TrialOutcome NoActionRequired(int index, TimeSpan total) =>
        new(index, "NoActionRequired", total, null);

    public static TrialOutcome WriteSkippedUnreliableSequence(int index, TimeSpan total) =>
        new(index, "WriteSkippedUnreliableSequence", total, null);

    public static TrialOutcome NeedsDecisionBlocked(int index, TimeSpan total, bool clipboardConfirmedStillRaw) =>
        new(index, $"NeedsDecisionBlocked:StillRaw={clipboardConfirmedStillRaw}", total, null);
}
