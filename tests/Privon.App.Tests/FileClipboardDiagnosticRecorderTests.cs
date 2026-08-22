using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// Phase 3C STEP41.1 -- FileClipboardDiagnosticRecorder's own TWO_LAYER_SINK regression: proves
// both IClipboardDiagnosticRecorder.Record (App-layer) and
// IClipboardMonitorDiagnosticSink.RecordWindowsEvent (Windows-layer) actually reach the same real
// file, clearly prefixed and never merged, plus the existing CONSTRUCTOR_FAILURE_ISOLATION/
// SINK_FAILURE_ISOLATION contract this type already had before this STEP. Uses a real temp file
// (deleted afterward) -- this is the one place in this project a real, disposable local file is
// the correct fixture (the type's whole purpose is writing one).
public class FileClipboardDiagnosticRecorderTests
{
    private static string NewTempFilePath() =>
        Path.Combine(Path.GetTempPath(), $"privon-diagnostic-test-{Guid.NewGuid():N}.log");

    // NOTE: reads always happen AFTER Dispose() has returned -- Dispose completes the channel and
    // bounded-waits for the background writer task to fully drain and close the file handle (see
    // this type's own Dispose doc), so there is no concurrent-open-handle race to poll around here
    // (unlike a real, long-running manual QA session, where AutoFlush keeps the file readable by a
    // separate process while this recorder's own handle stays open with FileShare.Read).

    [Fact]
    public void Record_WritesAppPrefixedLine_ContainingStageName()
    {
        var path = NewTempFilePath();
        try
        {
            var recorder = new FileClipboardDiagnosticRecorder(path);
            recorder.Record(ClipboardDiagnosticEvent.AttemptTerminal(7, ClipboardDiagnosticTerminalReason.Success));
            recorder.Dispose();

            var content = File.ReadAllText(path);
            Assert.Contains("app|", content);
            Assert.Contains(nameof(ClipboardDiagnosticStage.AttemptTerminal), content);
            Assert.DoesNotContain("windows|", content);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void RecordWindowsEvent_WritesWindowsPrefixedLine_ContainingKindName()
    {
        var path = NewTempFilePath();
        try
        {
            var recorder = new FileClipboardDiagnosticRecorder(path);
            recorder.RecordWindowsEvent(new ClipboardMonitorDiagnosticEvent(
                1, ClipboardMonitorDiagnosticKind.ExternalChangeRaised, 42, true));
            recorder.Dispose();

            var content = File.ReadAllText(path);
            Assert.Contains("windows|", content);
            Assert.Contains(nameof(ClipboardMonitorDiagnosticKind.ExternalChangeRaised), content);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void BothEventKinds_AppearAsSeparateClearlyPrefixedLines_NeverMerged()
    {
        var path = NewTempFilePath();
        try
        {
            var recorder = new FileClipboardDiagnosticRecorder(path);
            recorder.RecordWindowsEvent(new ClipboardMonitorDiagnosticEvent(
                1, ClipboardMonitorDiagnosticKind.NativeNotificationReceived, 42, true));
            recorder.RecordWindowsEvent(new ClipboardMonitorDiagnosticEvent(
                1, ClipboardMonitorDiagnosticKind.ExternalChangeRaised, 42, true));
            recorder.Record(ClipboardDiagnosticEvent.AppClipboardChangeReceived(1, 42, true, true));
            recorder.Dispose();

            var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
            Assert.Equal(3, lines.Count);
            Assert.Equal(2, lines.Count(l => l.StartsWith("windows|", StringComparison.Ordinal)));
            Assert.Equal(1, lines.Count(l => l.StartsWith("app|", StringComparison.Ordinal)));
        }
        finally
        {
            TryDelete(path);
        }
    }

    // ---- Phase 3C STEP41.2: the THIRD schema (ClipboardWriteDiagnosticEvent, the guarded-write
    // sequence-attribution boundary) appears under its own distinct "write|" prefix -- never
    // merged with "windows|" (the native-notification boundary) or "app|". ----
    [Fact]
    public void RecordWriteDiagnosticEvent_WritesWritePrefixedLine_ContainingKindName()
    {
        var path = NewTempFilePath();
        try
        {
            var recorder = new FileClipboardDiagnosticRecorder(path);
            recorder.RecordWriteDiagnosticEvent(new ClipboardWriteDiagnosticEvent(
                1, ClipboardWriteDiagnosticKind.VerificationSequenceObserved, null, 42, false, null, null, null));
            recorder.Dispose();

            var content = File.ReadAllText(path);
            Assert.Contains("write|", content);
            Assert.Contains(nameof(ClipboardWriteDiagnosticKind.VerificationSequenceObserved), content);
            Assert.DoesNotContain("windows|", content);
            Assert.DoesNotContain("app|", content);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void AllThreeEventKinds_AppearAsSeparateClearlyPrefixedLines_NeverMerged()
    {
        var path = NewTempFilePath();
        try
        {
            var recorder = new FileClipboardDiagnosticRecorder(path);
            recorder.RecordWindowsEvent(new ClipboardMonitorDiagnosticEvent(
                1, ClipboardMonitorDiagnosticKind.NativeNotificationReceived, 42, true));
            recorder.Record(ClipboardDiagnosticEvent.AppClipboardChangeReceived(1, 42, true, true));
            recorder.RecordWriteDiagnosticEvent(new ClipboardWriteDiagnosticEvent(
                1, ClipboardWriteDiagnosticKind.WriteStarted, 42, null, null, null, null, null));
            recorder.Dispose();

            var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
            Assert.Equal(3, lines.Count);
            Assert.Equal(1, lines.Count(l => l.StartsWith("windows|", StringComparison.Ordinal)));
            Assert.Equal(1, lines.Count(l => l.StartsWith("app|", StringComparison.Ordinal)));
            Assert.Equal(1, lines.Count(l => l.StartsWith("write|", StringComparison.Ordinal)));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void InvalidPath_ConstructorDoesNotThrow_RecordCallsAreSilentNoOps()
    {
        // An intentionally-unusable path (invalid directory) -- CONSTRUCTOR_FAILURE_ISOLATION.
        var invalidPath = Path.Combine(Path.GetTempPath(), "privon-diagnostic-test-\0-invalid", "x.log");

        using var recorder = new FileClipboardDiagnosticRecorder(invalidPath);
        recorder.Record(ClipboardDiagnosticEvent.AttemptTerminal(1, ClipboardDiagnosticTerminalReason.Success));
        recorder.RecordWindowsEvent(new ClipboardMonitorDiagnosticEvent(1, ClipboardMonitorDiagnosticKind.NativeNotificationReceived, 1, true));
        recorder.RecordWriteDiagnosticEvent(new ClipboardWriteDiagnosticEvent(1, ClipboardWriteDiagnosticKind.WriteStarted, 1, null, null, null, null, null));

        // No exception from construction or any Record call -- that is the entire assertion.
        recorder.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent_NeverThrows()
    {
        var path = NewTempFilePath();
        try
        {
            var recorder = new FileClipboardDiagnosticRecorder(path);
            recorder.Dispose();
            recorder.Dispose(); // second call must be a safe no-op
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void CreateDefaultFilePath_IsUnderLocalTempDirectory_WithExpectedPrefix()
    {
        var path = FileClipboardDiagnosticRecorder.CreateDefaultFilePath();

        Assert.StartsWith(Path.GetTempPath(), path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("privon-diagnostic-", Path.GetFileName(path));
        Assert.EndsWith(".log", path, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup only */ }
    }
}
