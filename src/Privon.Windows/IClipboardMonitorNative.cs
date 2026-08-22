namespace Privon.Windows;

/// <summary>
/// Classification of a message the owner thread's message loop retrieved -- deliberately just
/// enough to route the loop's own decisions (act on a clipboard change, exit on shutdown,
/// ignore everything else). Never carries clipboard content.
/// </summary>
internal enum ClipboardMonitorMessageKind
{
    ClipboardUpdate,
    Shutdown,
    ReadWork,
    WriteWork,
    Other,
}

/// <summary>
/// Phase 3A.4 STEP2 -- test seam over the Win32 clipboard-monitor primitives (Phase 3A.4 STEP1
/// report's TEST_STRATEGY: interop abstraction so <see cref="ClipboardChangeMonitor"/>'s
/// lifecycle/ordering/failure logic can be exercised deterministically without a real Windows
/// message-only window). The only production implementation is
/// <see cref="Win32ClipboardMonitorNative"/>; this interface itself carries no policy, only
/// the discrete OS operations <see cref="ClipboardChangeMonitor"/> needs to sequence.
///
/// Every method that can fail returns a bool and reports the Win32 error code via an out
/// parameter -- never an exception, never any clipboard content, per this phase's
/// SENSITIVE_DATA_RULES/FAILURE_MODEL findings. No OpenClipboard/CloseClipboard/
/// GetClipboardData/EmptyClipboard/SetClipboardData/GlobalAlloc-family member exists here --
/// those belong to a future transport STEP, not this listener foundation.
/// </summary>
internal interface IClipboardMonitorNative
{
    bool RegisterWindowClass(string className, out int win32Error);
    bool UnregisterWindowClass(string className, out int win32Error);
    bool CreateMessageOnlyWindow(string className, out nint hwnd, out int win32Error);
    bool DestroyWindow(nint hwnd, out int win32Error);
    bool AddClipboardFormatListener(nint hwnd, out int win32Error);
    bool RemoveClipboardFormatListener(nint hwnd, out int win32Error);

    /// <summary>
    /// Raw pass-through of GetClipboardSequenceNumber -- 0-vs-nonzero interpretation is
    /// deliberately NOT decided here (see <see cref="ClipboardChangeNotification"/>'s doc);
    /// this native layer reports facts only.
    /// </summary>
    uint GetClipboardSequenceNumber();

    bool IsUnicodeTextAvailable();
    bool PostShutdown(nint hwnd);

    /// <summary>
    /// Phase 3A.4 STEP3 -- posts the owner thread's message loop a wakeup for one pending
    /// <see cref="ClipboardChangeMonitor.ReadTextSnapshotAsync"/> request (Phase 3A.4 STEP2
    /// STEP1 report's cross-thread marshaling guidance: a custom WM_APP message posted via
    /// PostMessage, never SendMessage). This never carries clipboard content -- it is purely a
    /// "there is work waiting" wakeup; the owner thread dequeues the actual request itself.
    ///
    /// As of STEP3.1, failure is explicit (Win32 error reported via the out parameter) --
    /// PostMessageW itself can fail, and the caller must be able to complete the stranded
    /// request rather than silently leave it waiting for a wakeup that will never arrive.
    /// </summary>
    bool PostReadWorkSignal(nint hwnd, out int win32Error);

    /// <summary>
    /// Phase 3A.4 STEP4 -- posts the owner thread's message loop a wakeup for one pending
    /// <see cref="ClipboardChangeMonitor.WriteTextIfSequenceMatchesAsync"/> request. Same
    /// contract as <see cref="PostReadWorkSignal"/>: never carries clipboard content, a custom
    /// WM_APP message posted via PostMessage (never SendMessage), and failure is explicit via the
    /// out parameter so a stranded request can be completed rather than left waiting forever.
    /// </summary>
    bool PostWriteWorkSignal(nint hwnd, out int win32Error);

    ClipboardMonitorMessageKind WaitForNextMessage(nint hwnd);
}
