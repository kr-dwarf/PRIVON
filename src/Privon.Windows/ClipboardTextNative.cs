using System.Runtime.InteropServices;

namespace Privon.Windows;

/// <summary>
/// Phase 3A.4 STEP3 (read) / STEP4 (write) -- the only production implementation of
/// <see cref="IClipboardTextNative"/>. P/Invoke declarations scoped strictly to clipboard data
/// transport (Phase 3A.4 STEP1 report's NATIVE_INTEROP_BOUNDARY finding, applied again here) --
/// OpenClipboard/CloseClipboard/GetClipboardData/GlobalSize/GlobalLock/GlobalUnlock/GlobalAlloc/
/// GlobalFree/EmptyClipboard/SetClipboardData only.
/// </summary>
internal sealed class Win32ClipboardTextNative : IClipboardTextNative
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public bool OpenClipboard(nint hwnd, out int win32Error)
    {
        bool ok = NativeMethods.OpenClipboard(hwnd);
        win32Error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    public bool CloseClipboard(out int win32Error)
    {
        bool ok = NativeMethods.CloseClipboard();
        win32Error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    public bool TryGetUnicodeTextHandle(out nint handle, out int win32Error)
    {
        handle = NativeMethods.GetClipboardData(CF_UNICODETEXT);
        win32Error = handle == 0 ? Marshal.GetLastWin32Error() : 0;
        return handle != 0;
    }

    public bool TryGetGlobalSize(nint handle, out nuint size, out int win32Error)
    {
        // A 0 return is treated as a native failure here, not a legitimately-empty allocation --
        // a real CF_UNICODETEXT payload always needs at least the 2-byte NUL terminator, so 0
        // can never represent valid data (see the Phase 3A.4 STEP3 report's
        // GLOBALSIZE_ZERO_CLASSIFICATION finding).
        size = NativeMethods.GlobalSize(handle);
        win32Error = size == 0 ? Marshal.GetLastWin32Error() : 0;
        return size != 0;
    }

    public bool TryGlobalLock(nint handle, out nint pointer, out int win32Error)
    {
        pointer = NativeMethods.GlobalLock(handle);
        win32Error = pointer == 0 ? Marshal.GetLastWin32Error() : 0;
        return pointer != 0;
    }

    public bool TryGlobalUnlock(nint handle, out int win32Error)
    {
        // .NET's own P/Invoke marshaling layer (via [DllImport(SetLastError = true)] on
        // GlobalUnlock below) captures GetLastError() immediately after the native call
        // completes and stores it in a thread-local slot specific to that call, isolated from
        // any intervening managed code -- Marshal.GetLastWin32Error() below reads exactly that
        // captured value, not whatever the OS's raw TLS error happens to hold at the moment
        // this line runs. This is why no manual "clear the error before calling" step is needed
        // here (unlike hand-written C, where GetLastError() reads live OS state).
        bool stillLocked = NativeMethods.GlobalUnlock(handle);
        if (stillLocked)
        {
            // TRUE: lock count > 0 after the decrement -- not itself a failure in this type's
            // single-lock/single-unlock usage pattern (see ExecuteRead), so nothing to report.
            win32Error = 0;
            return true;
        }

        int lastError = Marshal.GetLastWin32Error();
        win32Error = lastError;
        return lastError == 0;
    }

    public bool TryGlobalAlloc(nuint byteSize, out nint handle, out int win32Error)
    {
        handle = NativeMethods.GlobalAlloc(GMEM_MOVEABLE, byteSize);
        win32Error = handle == 0 ? Marshal.GetLastWin32Error() : 0;
        return handle != 0;
    }

    public bool TryGlobalFree(nint handle, out int win32Error)
    {
        // GlobalFree returns NULL on success, the same (still-valid) handle on failure --
        // documented Microsoft contract, distinct from the GlobalUnlock TRUE/FALSE ambiguity
        // above (no GetLastError()-based disambiguation needed here).
        nint result = NativeMethods.GlobalFree(handle);
        win32Error = result == 0 ? 0 : Marshal.GetLastWin32Error();
        return result == 0;
    }

    public bool EmptyClipboard(out int win32Error)
    {
        bool ok = NativeMethods.EmptyClipboard();
        win32Error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    public bool TrySetClipboardData(nint handle, out int win32Error)
    {
        // SetClipboardData returns the handle of the data on success, NULL on failure.
        nint result = NativeMethods.SetClipboardData(CF_UNICODETEXT, handle);
        win32Error = result == 0 ? Marshal.GetLastWin32Error() : 0;
        return result != 0;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool OpenClipboard(nint hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint GetClipboardData(uint uFormat);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nuint GlobalSize(nint hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint GlobalLock(nint hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalUnlock(nint hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint GlobalFree(nint hMem);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetClipboardData(uint uFormat, nint hMem);
    }
}
