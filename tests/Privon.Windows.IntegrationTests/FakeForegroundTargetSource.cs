using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Deterministic, OS-free double for IForegroundTargetSource -- lets ForegroundTargetInspector's
// capture/failure-ordering logic (and, as of Phase 3A.5 STEP4, ClipboardChangeMonitor's
// FOREGROUND_EXECUTION_GUARD) be exercised without any real foreground window or process.
// Defaults to a fully successful resolution so tests only need to override the specific step
// they want to fail.
internal sealed class FakeForegroundTargetSource : IForegroundTargetSource
{
    public nint ForegroundWindowResult { get; set; } = 1;

    public bool WindowThreadProcessIdResult { get; set; } = true;
    public uint WindowThreadProcessIdValue { get; set; } = 4242;

    public bool ProcessNameResult { get; set; } = true;
    public string? ProcessNameValue { get; set; } = "ChatGPT";

    public List<string> CallLog { get; } = [];

    // CHECK1_VS_CHECK2 test seam (Phase 3A.5 STEP4): a "capture attempt" is one full
    // GetForegroundWindow -> TryGetWindowThreadProcessId -> TryGetProcessName sequence (exactly
    // what one CheckForegroundTarget call performs). CaptureAttemptCount increments once per
    // attempt, counted from GetForegroundWindow. When ChangeAfterAttempt is set, every attempt
    // AFTER that count uses the "*After" values instead of the defaults above -- letting a test
    // simulate CHECK 1 (attempt 1) matching and CHECK 2 (attempt 2) then failing, entirely
    // deterministically, with no real timing/threading involved. Unset (null, the default) means
    // every attempt always uses the values above -- every existing test that predates this seam
    // is unaffected.
    public int? ChangeAfterAttempt { get; set; }
    public nint ForegroundWindowResultAfter { get; set; } = 1;
    public bool WindowThreadProcessIdResultAfter { get; set; } = true;
    public uint WindowThreadProcessIdValueAfter { get; set; } = 4242;
    public bool ProcessNameResultAfter { get; set; } = true;
    public string? ProcessNameValueAfter { get; set; } = "ChatGPT";

    public int CaptureAttemptCount { get; private set; }

    private bool UseAfterValues => ChangeAfterAttempt.HasValue && CaptureAttemptCount > ChangeAfterAttempt.Value;

    public nint GetForegroundWindow()
    {
        CallLog.Add(nameof(GetForegroundWindow));
        CaptureAttemptCount++;
        return UseAfterValues ? ForegroundWindowResultAfter : ForegroundWindowResult;
    }

    public bool TryGetWindowThreadProcessId(nint hwnd, out uint processId)
    {
        CallLog.Add(nameof(TryGetWindowThreadProcessId));
        bool result = UseAfterValues ? WindowThreadProcessIdResultAfter : WindowThreadProcessIdResult;
        processId = result ? (UseAfterValues ? WindowThreadProcessIdValueAfter : WindowThreadProcessIdValue) : 0;
        return result;
    }

    public bool TryGetProcessName(uint processId, out string? processName)
    {
        CallLog.Add(nameof(TryGetProcessName));
        bool result = UseAfterValues ? ProcessNameResultAfter : ProcessNameResult;
        processName = result ? (UseAfterValues ? ProcessNameValueAfter : ProcessNameValue) : null;
        return result;
    }
}
