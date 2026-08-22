using System.Runtime.InteropServices;

namespace Privon.Windows;

/// <summary>
/// Phase 3A.4 STEP2 -- the only production implementation of <see cref="IClipboardMonitorNative"/>.
/// Every P/Invoke declaration here is scoped strictly to the "clipboard change monitor"
/// capability (message-only window + AddClipboardFormatListener + sequence/format queries) --
/// see the Phase 3A.4 STEP1 report's NATIVE_INTEROP_BOUNDARY finding: no shared "god"
/// NativeMethods file, no APIs speculatively added for the future hotkey/shield/UIA/transport
/// capabilities. OpenClipboard/CloseClipboard/GetClipboardData/SetClipboardData/EmptyClipboard/
/// GlobalAlloc-family functions deliberately do not appear here -- they belong to a future
/// transport STEP.
/// </summary>
internal sealed class Win32ClipboardMonitorNative : IClipboardMonitorNative
{
    private const uint WM_CLIPBOARDUPDATE = 0x031D;
    private const uint WM_APP = 0x8000;
    private const uint ShutdownMessageId = WM_APP + 1;
    private const uint ReadWorkMessageId = WM_APP + 2;
    private const uint WriteWorkMessageId = WM_APP + 3;
    private const uint CF_UNICODETEXT = 13;
    private static readonly nint HwndMessage = new(-3);

    // Kept alive as an instance field for this object's entire lifetime -- a WNDPROC delegate
    // handed to RegisterClassExW must never become eligible for GC while the window class
    // remains registered (Phase 3A.4 STEP1 report's WndProc-delegate-lifetime finding). This
    // object's lifetime already spans register-to-unregister, so a plain field is sufficient;
    // no GCHandle.Alloc pinning is needed for a plain delegate reference held this way.
    private readonly NativeMethods.WndProc _wndProc;

    public Win32ClipboardMonitorNative()
    {
        _wndProc = DefaultWndProc;
    }

    // This window never needs custom per-message behavior -- WM_CLIPBOARDUPDATE and the
    // shutdown message are both classified directly from the MSG the owner thread's GetMessageW
    // loop retrieves (see WaitForNextMessage), not from WndProc side effects. DefWindowProcW
    // handles everything else correctly, matching how a message-only window is meant to work.
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
        // HWND_MESSAGE as hWndParent is the documented way to create a message-only window:
        // not visible, no z-order, not enumerable, receives no broadcast messages -- exactly
        // what a clipboard-format-listener-only window needs and nothing more (Phase 3A.4
        // STEP1 report's LISTENER_HWND_STRATEGY finding).
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

    public bool AddClipboardFormatListener(nint hwnd, out int win32Error)
    {
        bool ok = NativeMethods.AddClipboardFormatListener(hwnd);
        win32Error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    public bool RemoveClipboardFormatListener(nint hwnd, out int win32Error)
    {
        bool ok = NativeMethods.RemoveClipboardFormatListener(hwnd);
        win32Error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    public uint GetClipboardSequenceNumber() => NativeMethods.GetClipboardSequenceNumber();

    public bool IsUnicodeTextAvailable() => NativeMethods.IsClipboardFormatAvailable(CF_UNICODETEXT);

    public bool PostShutdown(nint hwnd) =>
        NativeMethods.PostMessageW(hwnd, ShutdownMessageId, IntPtr.Zero, IntPtr.Zero);

    public bool PostReadWorkSignal(nint hwnd, out int win32Error)
    {
        bool ok = NativeMethods.PostMessageW(hwnd, ReadWorkMessageId, IntPtr.Zero, IntPtr.Zero);
        win32Error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    public bool PostWriteWorkSignal(nint hwnd, out int win32Error)
    {
        bool ok = NativeMethods.PostMessageW(hwnd, WriteWorkMessageId, IntPtr.Zero, IntPtr.Zero);
        win32Error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    public ClipboardMonitorMessageKind WaitForNextMessage(nint hwnd)
    {
        // GetMessageW's return is a three-way signal (>0 normal, 0 on WM_QUIT, -1 on error),
        // not a plain bool -- captured as int deliberately, unlike a naive bool declaration.
        // We never post WM_QUIT ourselves (shutdown uses our own message id below), so a 0/-1
        // here only happens from an unexpected condition -- treated as Shutdown defensively so
        // the loop always exits rather than spinning.
        int result = NativeMethods.GetMessageW(out var msg, hwnd, 0, 0);
        if (result <= 0) return ClipboardMonitorMessageKind.Shutdown;

        NativeMethods.TranslateMessage(ref msg);
        NativeMethods.DispatchMessageW(ref msg);

        if (msg.message == WM_CLIPBOARDUPDATE) return ClipboardMonitorMessageKind.ClipboardUpdate;
        if (msg.message == ShutdownMessageId) return ClipboardMonitorMessageKind.Shutdown;
        if (msg.message == ReadWorkMessageId) return ClipboardMonitorMessageKind.ReadWork;
        if (msg.message == WriteWorkMessageId) return ClipboardMonitorMessageKind.WriteWork;
        return ClipboardMonitorMessageKind.Other;
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
        public static extern bool AddClipboardFormatListener(nint hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RemoveClipboardFormatListener(nint hwnd);

        [DllImport("user32.dll")]
        public static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")]
        public static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        public static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern nint DispatchMessageW(ref MSG lpMsg);
    }
}
