namespace Privon.Windows;

/// <summary>
/// Phase 3A.5 STEP2 -- Foreground Target Mechanical Primitive. Captures a single, fresh
/// <see cref="ForegroundTargetSnapshot"/> fact about the current foreground window/process, on
/// demand, with zero caching between calls and zero clipboard content APIs called anywhere in
/// this type or its dependencies.
///
/// This type knows nothing about product/privacy policy -- no "ChatGPT," no "supported target,"
/// no "eligible," no notion of PII/Protect/Alias/Risk. A future <c>Privon.App</c>-owned TargetGate
/// is the only place <see cref="ForegroundTargetSnapshot.ProcessName"/> is ever compared against a
/// configured target name (CHATGPT PROCESS NAME POLICY: that comparison, and its
/// <c>StringComparison.OrdinalIgnoreCase</c> choice, live entirely outside this type).
///
/// FOREGROUND_EXECUTION_GUARD (Phase 3A.5 STEP1 correction, recorded but NOT implemented by this
/// type): a single <see cref="Capture"/> call from an App-thread policy layer, by itself, cannot
/// eliminate the cross-thread-marshaling TOCTOU gap between "target checked eligible" and "raw
/// clipboard read/write actually executes" -- a future guarded read/write still needs its OWN
/// fresh foreground-PID-equality check performed on the clipboard owner thread, immediately before
/// OpenClipboard/EmptyClipboard, comparing against an explicit expected PID the App layer already
/// decided was eligible. This type is a building block for that future guard, not the guard
/// itself; it exists purely to answer "what is the foreground process right now," synchronously,
/// with no side effects.
/// </summary>
public sealed class ForegroundTargetInspector
{
    private readonly IForegroundTargetSource _source;

    public ForegroundTargetInspector() : this(new Win32ForegroundTargetSource())
    {
    }

    internal ForegroundTargetInspector(IForegroundTargetSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <summary>
    /// CAPTURE_FLOW: GetForegroundWindow -> GetWindowThreadProcessId -> process-name-by-PID, using
    /// the CURRENT PID at each step -- never a cached/enumerated PID set
    /// (PRODUCTION_TARGET_PID_STRATEGY). Any ordinary failure at any step (no foreground window,
    /// unresolved/zero PID, or the process having exited before its name could be looked up)
    /// short-circuits to an unresolved snapshot -- never a stale prior snapshot, never a guess.
    /// Never throws for an ordinary condition; never calls any clipboard content API.
    /// </summary>
    public ForegroundTargetSnapshot Capture()
    {
        nint hwnd = _source.GetForegroundWindow();
        if (hwnd == 0)
            return default;

        // A defensive processId==0 check here, in addition to a false return, means this type
        // never trusts a zero PID as valid even if a (buggy or future) IForegroundTargetSource
        // implementation ever reported success alongside one -- 0 is never a real process ID.
        if (!_source.TryGetWindowThreadProcessId(hwnd, out uint processId) || processId == 0)
            return default;

        if (!_source.TryGetProcessName(processId, out string? processName))
            return default;

        return new ForegroundTargetSnapshot(IsResolved: true, ProcessId: processId, ProcessName: processName);
    }
}
