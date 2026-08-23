namespace Privon.App.Tests;

// Stabilization Gate (post-Phase-0.2E) -- deterministic proof tests for the two root causes
// identified for INTERMITTENT_FULL_SOLUTION_TEST_FLAKE, written BEFORE the corresponding fixes
// (per the stabilization-gate instruction's own step E). Neither test here depends on OS
// scheduler luck, real Win32 resources, or any production coordinator/composition code -- both
// are fully self-contained, 100% reproducible every run via explicit synchronization primitives
// (ManualResetEventSlim), never Task.Delay/sleep-based timing guesses.
//
// ROOT CAUSE 1 (ORDERING_HAZARD): several hand-written Fake* test doubles in this project
// (FakeClipboardWriteTransport/FakeClipboardPrivacyProcessor/FakeClipboardComposerVerificationHandoff/
// FakeClipboardDecisionSessionPublisher) incremented their own "CallCount" completion signal
// BEFORE populating the companion Received*/List<T> fields a test later reads. A coordinator
// test that polls only CallCount via WaitUntilAsync (10ms interval) and then immediately reads a
// companion list is racy: the coordinator's own background worker thread executes those two
// writes as two separate statements, and the OS scheduler CAN preempt that thread between them.
// This is not a CPU/JIT memory-reordering question -- .NET always executes ONE thread's own
// instructions in program order -- it is a genuine wall-clock race: whichever statement is
// written LAST is the only one whose visibility a concurrent poller can safely treat as "everything
// that happens-before it, in program order, is also already done." Class ClipboardTestDoubleOrderingHazardTests
// below proves this mechanism directly.
//
// ROOT CAUSE 2 (SHARED_TEMP_GLOB_COLLISION): PrivonAppCompositionTests's diagnostics-file-count
// assertions (SnapshotDiagnosticLogFiles) matched ANY file under %TEMP% named "privon-diagnostic-*.log"
// -- a glob FileClipboardDiagnosticRecorderTests's own fixture naming ("privon-diagnostic-test-{guid}.log")
// also matches. Because xUnit runs different test classes in parallel by default, and both
// classes write/delete real files in this SAME shared %TEMP% directory, a
// PrivonAppCompositionTests before/after snapshot pair can observe an unrelated
// FileClipboardDiagnosticRecorderTests fixture file appear or disappear in between -- entirely
// independent of ForegroundChangeMonitor/production composition wiring. Class
// DiagnosticLogGlobCollisionTests below proves the collision and the fix's exclusion.
public class ClipboardTestDoubleOrderingHazardTests
{
    // A minimal, self-contained double reproducing the EXACT vulnerable shape the four affected
    // Fakes had: writes the "signal" field first, then (only once explicitly released) writes the
    // "payload" field. ManualResetEventSlim makes the gap between the two writes deterministic and
    // reproducible on every run -- no reliance on OS scheduler timing.
    private sealed class SignalFirstDouble
    {
        private readonly ManualResetEventSlim _releaseSecondWrite = new(initialState: false);
        public volatile int CallCount;
        public volatile string? Payload;

        public void RecordSignalFirst()
        {
            CallCount = 1;
            _releaseSecondWrite.Wait();
            Payload = "done";
        }

        public void ReleaseSecondWrite() => _releaseSecondWrite.Set();
    }

    [Fact]
    public async Task SignalWrittenBeforePayload_ConcurrentReaderCanObserveCallCountWithoutPayload()
    {
        var d = new SignalFirstDouble();
        var writer = Task.Run(d.RecordSignalFirst);

        // Deterministic: the writer is guaranteed to be blocked at _releaseSecondWrite.Wait() by
        // the time CallCount becomes visible (it is the only prior statement) -- so this
        // SpinUntil, once it observes CallCount == 1, is observing a state the writer will hold
        // open until explicitly released below. This is exactly the shape a WaitUntilAsync poll +
        // immediate list-index assertion hit in the real coordinator tests.
        Assert.True(SpinWait.SpinUntil(() => d.CallCount == 1, TimeSpan.FromSeconds(5)));

        // THE HAZARD: CallCount is already 1, but Payload -- the companion state a test would
        // read next -- is still unset. A test asserting on Payload right here would fail, exactly
        // like `Assert.Same(plan, publisher.ReceivedDecisionPlans[0])` failed with
        // ArgumentOutOfRangeException after `WaitUntilAsync(() => publisher.CallCount >= 1)`.
        Assert.Null(d.Payload);

        d.ReleaseSecondWrite();
        await writer;
        Assert.Equal("done", d.Payload);
    }

    // The companion double using the FIXED ordering: payload written first, signal written last.
    private sealed class SignalLastDouble
    {
        private readonly ManualResetEventSlim _writerReachedCheckpoint = new(initialState: false);
        private readonly ManualResetEventSlim _releaseSignalWrite = new(initialState: false);
        public volatile int CallCount;
        public volatile string? Payload;

        public void RecordPayloadFirst()
        {
            Payload = "done";
            _writerReachedCheckpoint.Set();
            _releaseSignalWrite.Wait();
            CallCount = 1;
        }

        public void WaitUntilWriterReachedCheckpoint() => _writerReachedCheckpoint.Wait();
        public void ReleaseSignalWrite() => _releaseSignalWrite.Set();
    }

    [Fact]
    public async Task SignalWrittenAfterPayload_PayloadIsAlwaysAlreadyPopulatedBeforeSignalCanBeObserved()
    {
        var d = new SignalLastDouble();
        var writer = Task.Run(d.RecordPayloadFirst);

        // Deterministic checkpoint (not a timing guess): the writer signals explicitly that it has
        // ALREADY written Payload and is now blocked immediately before writing CallCount.
        d.WaitUntilWriterReachedCheckpoint();

        // Payload is unconditionally already populated here -- CallCount is still 0, proving no
        // reader could possibly have observed CallCount == 1 without Payload also being set,
        // because CallCount write is held open strictly AFTER Payload write in program order.
        Assert.Equal("done", d.Payload);
        Assert.Equal(0, d.CallCount);

        d.ReleaseSignalWrite();
        Assert.True(SpinWait.SpinUntil(() => d.CallCount == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal("done", d.Payload);

        await writer;
    }
}

public class DiagnosticLogGlobCollisionTests
{
    // Mirrors PrivonAppCompositionTests's OWN pre-fix helper exactly (broad glob, no exclusion) --
    // proves the collision this stabilization gate is fixing.
    private static string[] SnapshotWithBroadGlob() =>
        Directory.GetFiles(Path.GetTempPath(), "privon-diagnostic-*.log");

    // Mirrors the FIXED helper -- excludes FileClipboardDiagnosticRecorderTests's own fixture
    // naming convention ("privon-diagnostic-test-*.log"), which never overlaps the real
    // production FileClipboardDiagnosticRecorder.CreateDefaultFilePath() shape
    // ("privon-diagnostic-{yyyyMMdd-HHmmss}.log").
    private static string[] SnapshotWithFixedGlob() =>
        Directory.GetFiles(Path.GetTempPath(), "privon-diagnostic-*.log")
            .Where(p => !Path.GetFileName(p).Contains("-test-", StringComparison.Ordinal))
            .ToArray();

    [Fact]
    public void BroadGlob_CountsAnUnrelatedFixtureFile_FromTheOtherTestClassesOwnNamingConvention()
    {
        // Simulates exactly what FileClipboardDiagnosticRecorderTests.NewTempFilePath() produces --
        // a file this stabilization gate's OWN composition-root diagnostics tests must never count.
        var unrelatedFixtureFile = Path.Combine(Path.GetTempPath(), $"privon-diagnostic-test-{Guid.NewGuid():N}.log");
        File.WriteAllText(unrelatedFixtureFile, string.Empty);
        try
        {
            var before = SnapshotWithBroadGlob();
            Assert.Contains(unrelatedFixtureFile, before);

            // THE COLLISION: PrivonAppCompositionTests's pre-fix before/after diffing would see
            // this file appear as a "new diagnostic log" even though it has nothing to do with the
            // composition root under test -- exactly the ExplicitDiagnosticsEnabled_.../
            // ProductionConstructor_WithNoEnvironmentVariableSet_... failures observed.
        }
        finally
        {
            File.Delete(unrelatedFixtureFile);
        }
    }

    [Fact]
    public void FixedGlob_NeverCountsTheOtherTestClassesOwnFixtureFile()
    {
        var unrelatedFixtureFile = Path.Combine(Path.GetTempPath(), $"privon-diagnostic-test-{Guid.NewGuid():N}.log");
        File.WriteAllText(unrelatedFixtureFile, string.Empty);
        try
        {
            var snapshot = SnapshotWithFixedGlob();
            Assert.DoesNotContain(unrelatedFixtureFile, snapshot);
        }
        finally
        {
            File.Delete(unrelatedFixtureFile);
        }
    }

    [Fact]
    public void FixedGlob_StillCountsARealProductionShapedDiagnosticFile()
    {
        // Confirms the fix does not accidentally exclude the real production naming shape
        // (FileClipboardDiagnosticRecorder.CreateDefaultFilePath()'s own
        // "privon-diagnostic-{yyyyMMdd-HHmmss}.log" pattern -- no "-test-" infix).
        var realShapedFile = Path.Combine(Path.GetTempPath(), $"privon-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        File.WriteAllText(realShapedFile, string.Empty);
        try
        {
            var snapshot = SnapshotWithFixedGlob();
            Assert.Contains(realShapedFile, snapshot);
        }
        finally
        {
            File.Delete(realShapedFile);
        }
    }
}
