namespace Privon.Windows;

/// <summary>
/// Phase 0.2B STEP57 -- test seam over the Win32 foreground-change-notification primitives, mirroring
/// <see cref="ISessionLockNative"/>'s own exact pattern (Phase 3A.4 STEP1 report's TEST_STRATEGY:
/// interop abstraction so <see cref="ForegroundChangeMonitor"/>'s lifecycle/ordering/failure logic can
/// be exercised deterministically without a real Windows message-only window or a real SetWinEventHook
/// registration). The only production implementation is <see cref="Win32ForegroundChangeNative"/>; this
/// interface itself carries no policy -- it knows nothing about ChatGPT/TargetGate/clipboard/Detection/
/// App policy, only the discrete OS operations <see cref="ForegroundChangeMonitor"/> needs to sequence.
///
/// Every method that can fail returns a bool and reports the Win32 error code via an out parameter --
/// never an exception, matching <see cref="ISessionLockNative"/>'s identical FAILURE_MODEL. No
/// HWND/PID/process-name/window-title member of any kind exists here beyond the message-only window's
/// own opaque handle -- this seam structurally cannot expose foreground-window identity.
/// </summary>
internal interface IForegroundChangeNative
{
    bool RegisterWindowClass(string className, out int win32Error);
    bool UnregisterWindowClass(string className, out int win32Error);
    bool CreateMessageOnlyWindow(string className, out nint hwnd, out int win32Error);
    bool DestroyWindow(nint hwnd, out int win32Error);

    /// <summary>Installs the system-wide EVENT_SYSTEM_FOREGROUND WinEvent hook (WINEVENT_OUTOFCONTEXT)
    /// FROM the calling thread -- per Microsoft's documented SetWinEventHook contract ("the client
    /// thread that calls SetWinEventHook must have a message loop in order to receive events" / "for
    /// out-of-context events, the event is delivered on the same thread that called SetWinEventHook"),
    /// the calling thread must be the exact same thread that subsequently pumps
    /// <see cref="WaitForNextMessage"/>. <paramref name="hwnd"/> is the message-only window this
    /// monitor's own custom messages are posted to and filtered on -- SetWinEventHook itself takes no
    /// HWND parameter; <paramref name="hwnd"/> is only used by the installed native callback to know
    /// where to PostMessageW its own translated notification.</summary>
    bool SetHook(nint hwnd, out int win32Error);

    /// <summary>Removes the hook installed by <see cref="SetHook"/> -- MUST be called from the exact
    /// same thread that called <see cref="SetHook"/> (Microsoft's documented UnhookWinEvent contract:
    /// "Call this function from the same thread that installed the event hook. UnhookWinEvent fails if
    /// called from a thread different from the call that corresponds to SetWinEventHook.").</summary>
    bool Unhook(out int win32Error);

    bool PostShutdown(nint hwnd);

    ForegroundChangeMessageKind WaitForNextMessage(nint hwnd);
}
