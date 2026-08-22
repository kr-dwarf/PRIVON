using System.Runtime.InteropServices;

namespace Privon.Windows;

/// <summary>
/// Phase 3C STEP38 -- the only production implementation of <see cref="ISessionLockNative"/>.
/// Every P/Invoke declaration here is scoped strictly to the "session-lock notification" capability
/// (message-only window + WTSRegisterSessionNotification + WM_WTSSESSION_CHANGE translation) --
/// matching <see cref="Win32ClipboardMonitorNative"/>'s own NATIVE_INTEROP_BOUNDARY finding (Phase
/// 3A.4 STEP1 report) applied to this new capability: no shared "god" NativeMethods file, so the
/// window-class/message-loop P/Invoke declarations below are DELIBERATELY a second, independent
/// copy of the equivalent declarations in <see cref="Win32ClipboardMonitorNative"/> rather than an
/// extracted shared helper -- exactly the same intentional-duplication precedent Phase 3C STEP29
/// already established for <c>Win32ComposerTextSource</c>'s own foreground-check reimplementation.
///
/// Win32 constants below were verified against live Microsoft Learn documentation (not memory) at
/// implementation time:
/// WTSRegisterSessionNotification/WTSUnRegisterSessionNotification (wtsapi32.h, Wtsapi32.dll),
/// WM_WTSSESSION_CHANGE = 0x02B1 (Winuser.h), NOTIFY_FOR_THIS_SESSION = 0x0,
/// WTS_SESSION_LOCK = 0x7.
/// </summary>
internal sealed class Win32SessionLockNative : ISessionLockNative
{
    private const uint WM_APP = 0x8000;
    private const uint ShutdownMessageId = WM_APP + 1;
    private const uint WM_WTSSESSION_CHANGE = 0x02B1;
    private const uint NOTIFY_FOR_THIS_SESSION = 0x0;
    private const nint WTS_SESSION_LOCK = 0x7;
    private static readonly nint HwndMessage = new(-3);

    // Kept alive as an instance field for this object's entire lifetime -- a WNDPROC delegate
    // handed to RegisterClassExW must never become eligible for GC while the window class remains
    // registered (same WndProc-delegate-lifetime finding Win32ClipboardMonitorNative already
    // documents).
    private readonly NativeMethods.WndProc _wndProc;

    public Win32SessionLockNative()
    {
        _wndProc = DefaultWndProc;
    }

    // This window never needs custom per-message behavior -- WM_WTSSESSION_CHANGE and the shutdown
    // message are both classified directly from the MSG the owner thread's GetMessageW loop
    // retrieves (see WaitForNextMessage), not from WndProc side effects.
    private static nint DefaultWndProc(nint hWnd, uint msg, nint wParam, nint lParam) =>
        NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);

    public bool RegisterWindowClass(string className, out int win32Error)
    {
        var wc = new NativeMethods.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = NativeMethods.GetModuleHandleW(null),
            lpszClassName = className,
        };
        ushort atom = NativeMethods.RegisterClassExW(ref wc);
        win32Error = atom == 0 ? Marshal.GetLastWin32Error() : 0;
        return atom != 0;
    }

    public bool UnregisterWindowClass(string className, out int win32Error)
    {
        bool ok = NativeMethods.UnregisterClassW(className, NativeMethods.GetModuleHandleW(null));
        win32Error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    public bool CreateMessageOnlyWindow(string className, out nint hwnd, out int win32Error)
    {
        // HWND_MESSAGE -- same message-only-window rationale as Win32ClipboardMonitorNative's own
        // LISTENER_HWND_STRATEGY: not visible, no z-order, not enumerable, receives no broadcast
        // messages. WTSRegisterSessionNotification only requires an HWND with a message loop --
        // Microsoft's own documentation does not require it to be a visible top-level window.
        hwnd = NativeMethods.CreateWindowExW(
            0, className, className, 0, 0, 0, 0, 0,
            HwndMessage, IntPtr.Zero, NativeMethods.GetModuleHandleW(null), IntPtr.Zero);
        win32Error = hwnd == 0 ? Marshal.GetLastWin32Error() : 0;
        return hwnd != 0;
    }

    public bool DestroyWindow(nint hwnd, out int win32Error)
    {
        bool ok = NativeMethods.DestroyWindow(hwnd);
        win32Error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    public bool RegisterSessionNotification(nint hwnd, out int win32Error)
    {
        bool ok = NativeMethods.WTSRegisterSessionNotification(hwnd, NOTIFY_FOR_THIS_SESSION);
        win32Error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    public bool UnregisterSessionNotification(nint hwnd, out int win32Error)
    {
        bool ok = NativeMethods.WTSUnRegisterSessionNotification(hwnd);
        win32Error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    public bool PostShutdown(nint hwnd) =>
        NativeMethods.PostMessageW(hwnd, ShutdownMessageId, IntPtr.Zero, IntPtr.Zero);

    public SessionLockMessageKind WaitForNextMessage(nint hwnd)
    {
        // Same three-way GetMessageW signal handling as Win32ClipboardMonitorNative's own
        // WaitForNextMessage -- never posts WM_QUIT ourselves, so a 0/-1 result here only happens
        // from an unexpected condition and is treated as Shutdown defensively.
        int result = NativeMethods.GetMessageW(out var msg, hwnd, 0, 0);
        if (result <= 0) return SessionLockMessageKind.Shutdown;

        NativeMethods.TranslateMessage(ref msg);
        NativeMethods.DispatchMessageW(ref msg);

        if (msg.message == ShutdownMessageId) return SessionLockMessageKind.Shutdown;
        if (msg.message == WM_WTSSESSION_CHANGE && msg.wParam == WTS_SESSION_LOCK) return SessionLockMessageKind.Locked;
        return SessionLockMessageKind.Other;
    }

    private static class NativeMethods
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public nint lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public nint hInstance;
            public nint hIcon;
            public nint hCursor;
            public nint hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
            public nint hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public nint hwnd;
            public uint message;
            public nint wParam;
            public nint lParam;
            public uint time;
            public POINT pt;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool UnregisterClassW(string lpClassName, nint hInstance);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern nint CreateWindowExW(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyWindow(nint hWnd);

        [DllImport("user32.dll")]
        public static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern nint GetModuleHandleW(string? lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        public static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern nint DispatchMessageW(ref MSG lpMsg);

        // Wtsapi32.dll -- verified signature: BOOL WTSRegisterSessionNotification([in] HWND hWnd,
        // [in] DWORD dwFlags). "For every call to this function, there must be a corresponding call
        // to WTSUnRegisterSessionNotification" (Microsoft Learn) -- enforced by SessionLockMonitor's
        // own paired register/unregister lifecycle.
        [DllImport("Wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSRegisterSessionNotification(nint hWnd, uint dwFlags);

        [DllImport("Wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSUnRegisterSessionNotification(nint hWnd);
    }
}
