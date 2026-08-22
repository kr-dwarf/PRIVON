namespace Privon.Windows;

/// <summary>
/// Phase 3C STEP38 -- test seam over the Win32 session-lock-notification primitives, mirroring
/// <see cref="IClipboardMonitorNative"/>'s own exact pattern (Phase 3A.4 STEP1 report's
/// TEST_STRATEGY: interop abstraction so <see cref="SessionLockMonitor"/>'s lifecycle/ordering/
/// failure logic can be exercised deterministically without a real Windows message-only window or
/// a real session lock/unlock). The only production implementation is
/// <see cref="Win32SessionLockNative"/>; this interface itself carries no policy -- it knows
/// nothing about <c>ClipboardDecisionScopeLifecycle</c>/<c>ClipboardComposerVerifier</c>/App policy,
/// only the discrete OS operations <see cref="SessionLockMonitor"/> needs to sequence.
///
/// Every method that can fail returns a bool and reports the Win32 error code via an out
/// parameter -- never an exception, matching <see cref="IClipboardMonitorNative"/>'s identical
/// FAILURE_MODEL. No username/session-name/process-info member exists here -- session identity is
/// entirely implicit in the OS registering THIS window for THIS session's notifications only.
/// </summary>
internal interface ISessionLockNative
{
    bool RegisterWindowClass(string className, out int win32Error);
    bool UnregisterWindowClass(string className, out int win32Error);
    bool CreateMessageOnlyWindow(string className, out nint hwnd, out int win32Error);
    bool DestroyWindow(nint hwnd, out int win32Error);

    /// <summary>Raw pass-through of <c>WTSRegisterSessionNotification(hwnd, NOTIFY_FOR_THIS_SESSION)</c>
    /// -- THIS session only, never <c>NOTIFY_FOR_ALL_SESSIONS</c> (Phase 3C STEP37's frozen 0.1
    /// scope: a single-user desktop app has no legitimate reason to observe another session's lock
    /// state).</summary>
    bool RegisterSessionNotification(nint hwnd, out int win32Error);

    bool UnregisterSessionNotification(nint hwnd, out int win32Error);

    bool PostShutdown(nint hwnd);

    /// <summary>Blocks until the next message the owner thread's window queue receives, already
    /// classified (see <see cref="SessionLockMessageKind"/>) -- <see cref="SessionLockMessageKind.Locked"/>
    /// only for a <c>WM_WTSSESSION_CHANGE</c> message whose <c>wParam</c> is exactly
    /// <c>WTS_SESSION_LOCK</c> (0x7); every other WTS reason code (including <c>WTS_SESSION_UNLOCK</c>,
    /// 0x8) classifies as <see cref="SessionLockMessageKind.Other"/> and is silently ignored by the
    /// caller.</summary>
    SessionLockMessageKind WaitForNextMessage(nint hwnd);
}
