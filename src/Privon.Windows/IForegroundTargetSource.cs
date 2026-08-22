namespace Privon.Windows;

/// <summary>
/// Phase 3A.5 STEP2 -- test seam over the Win32/BCL foreground-window-identity primitives
/// (GetForegroundWindow/GetWindowThreadProcessId/process-name-by-PID lookup), matching this
/// codebase's existing native-seam pattern (<see cref="IClipboardMonitorNative"/>,
/// <see cref="IClipboardTextNative"/>): an interface with no policy of its own, so
/// <see cref="ForegroundTargetInspector"/>'s ordering/failure logic can be exercised
/// deterministically without any real foreground window or process. The only production
/// implementation is <see cref="Win32ForegroundTargetSource"/>.
///
/// No OpenClipboard/GetClipboardData/SetClipboardData/EmptyClipboard-family member exists here,
/// and never will -- this seam only ever inspects foreground-window/process metadata, per this
/// STEP's explicit PRIVACY scope (zero clipboard content APIs called anywhere in this capability).
/// </summary>
internal interface IForegroundTargetSource
{
    /// <summary>
    /// Raw pass-through of GetForegroundWindow(). A return of 0 is a normal, expected outcome
    /// (no window currently has foreground, e.g. the lock screen) -- not a failure to be
    /// interpreted further here.
    /// </summary>
    nint GetForegroundWindow();

    /// <summary>
    /// Raw pass-through of GetWindowThreadProcessId(hwnd, out pid). Returns false for any
    /// ordinary failure to resolve a process ID (invalid HWND, native call failure, or a
    /// resolved-but-zero PID) -- <paramref name="processId"/> is 0 whenever this returns false.
    /// </summary>
    bool TryGetWindowThreadProcessId(nint hwnd, out uint processId);

    /// <summary>
    /// Resolves the executable's process name for a given PID. Returns false for any ordinary
    /// failure to resolve it -- most notably the process having exited between the caller
    /// capturing its PID and this lookup running, which is an expected race, not a crash.
    /// <paramref name="processName"/> is null whenever this returns false.
    /// </summary>
    bool TryGetProcessName(uint processId, out string? processName);
}
