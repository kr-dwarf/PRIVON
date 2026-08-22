namespace Privon.Windows;

/// <summary>
/// Phase 3A.4 STEP3 (read) / STEP4 (write) -- test seam over the Win32 clipboard *data
/// transport* primitives (OpenClipboard/CloseClipboard/GetClipboardData/GlobalSize/GlobalLock/
/// GlobalUnlock, and -- as of STEP4 -- GlobalAlloc/GlobalFree/EmptyClipboard/SetClipboardData).
/// Deliberately a separate interface from <see cref="IClipboardMonitorNative"/>: window/listener
/// lifecycle and clipboard data transport are different responsibilities (Phase 3A.4 STEP3
/// instruction's own guidance), so they are not mixed into one native seam. The only production
/// implementation is <see cref="Win32ClipboardTextNative"/>.
/// </summary>
internal interface IClipboardTextNative
{
    bool OpenClipboard(nint hwnd, out int win32Error);
    bool CloseClipboard(out int win32Error);

    /// <summary>
    /// Wraps GetClipboardData(CF_UNICODETEXT). Callers must already have confirmed format
    /// availability before calling this -- this member does not itself decide whether the format
    /// is present.
    /// </summary>
    bool TryGetUnicodeTextHandle(out nint handle, out int win32Error);

    bool TryGetGlobalSize(nint handle, out nuint size, out int win32Error);
    bool TryGlobalLock(nint handle, out nint pointer, out int win32Error);

    /// <summary>
    /// Phase 3A.4 STEP3.1 -- GlobalUnlock's own boolean return is ambiguous by Microsoft's
    /// documented contract: TRUE means the object is still locked (lock count > 0 after the
    /// decrement); FALSE means EITHER the lock count reached zero (normal, successful) OR a
    /// genuine failure -- the two FALSE cases are only distinguishable via GetLastError()
    /// captured immediately afterward (NO_ERROR = normal; anything else = real failure). This
    /// member performs that distinction itself, so callers never see the ambiguous raw bool:
    /// returns true for "no failure" (TRUE-still-locked or FALSE+NO_ERROR), false only for a
    /// genuine FALSE+non-zero-error failure, with the Win32 error reported via the out parameter.
    /// </summary>
    bool TryGlobalUnlock(nint handle, out int win32Error);

    /// <summary>
    /// Phase 3A.4 STEP4 -- allocates a GMEM_MOVEABLE block sized <paramref name="byteSize"/> for
    /// the replacement CF_UNICODETEXT payload. Always called (and, on any subsequent failure
    /// before <see cref="TrySetClipboardData"/> succeeds, always freed via
    /// <see cref="TryGlobalFree"/>) BEFORE <see cref="OpenClipboard"/> -- the STEP4 contract's
    /// HGLOBAL_PREALLOCATION_VS_MS_SAMPLE_ORDERING decision, to keep the clipboard-open window as
    /// short as possible.
    /// </summary>
    bool TryGlobalAlloc(nuint byteSize, out nint handle, out int win32Error);

    /// <summary>
    /// Phase 3A.4 STEP4 -- frees a handle PRIVON still owns. Ownership is one-directional and
    /// permanent: this must NEVER be called on a handle after <see cref="TrySetClipboardData"/>
    /// has returned true for it (the system owns it from that point on) -- see
    /// <see cref="ClipboardChangeMonitor"/>'s write-flow doc for the full ownership state
    /// machine. A cleanup failure here is surfaced, never silently swallowed
    /// (GLOBALFREE_LEAK_VISIBILITY).
    /// </summary>
    bool TryGlobalFree(nint handle, out int win32Error);

    /// <summary>
    /// Phase 3A.4 STEP4 -- wraps EmptyClipboard(). Success is the DESTRUCTIVE_BOUNDARY: the
    /// instant this returns true, the original clipboard content is already gone and cannot be
    /// rolled back (this layer never caches original content for that purpose).
    /// </summary>
    bool EmptyClipboard(out int win32Error);

    /// <summary>
    /// Phase 3A.4 STEP4 -- wraps SetClipboardData(CF_UNICODETEXT, handle). A true return means
    /// the system now owns <paramref name="handle"/> -- <see cref="TryGlobalFree"/> must never be
    /// called on it again, by anyone, for any reason.
    /// </summary>
    bool TrySetClipboardData(nint handle, out int win32Error);
}
