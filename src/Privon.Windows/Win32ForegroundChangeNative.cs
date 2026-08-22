using System.Runtime.InteropServices;

namespace Privon.Windows;

/// <summary>
/// Phase 0.2B STEP57 -- the only production implementation of <see cref="IForegroundChangeNative"/>.
/// Every P/Invoke declaration here is scoped strictly to the "foreground-change notification"
/// capability (message-only window + SetWinEventHook/UnhookWinEvent + WinEventProc translation) --
/// matching <see cref="Win32ClipboardMonitorNative"/>'s/<see cref="Win32SessionLockNative"/>'s own
/// NATIVE_INTEROP_BOUNDARY finding applied to this new capability: no shared "god" NativeMethods file,
/// so the window-class/message-loop P/Invoke declarations below are DELIBERATELY a third, independent
/// copy of the equivalent declarations in those two types rather than an extracted shared helper.
///
/// MICROSOFT_LEARN_CONTRACT (STEP57, verified against live Microsoft Learn documentation at
/// implementation time -- see <see cref="ForegroundChangeMonitor"/>'s own class doc for the full quoted
/// text and source URLs): SetWinEventHook/UnhookWinEvent both require the SAME calling thread for
/// install/pump/remove (enforced structurally by <see cref="ForegroundChangeMonitor.OwnerThreadMain"/>
/// calling <see cref="SetHook"/> and <see cref="Unhook"/> itself, never a separate thread), and "you
/// should use the GCHandle structure to avoid exceptions [...] this tells the garbage collector not to
/// move the callback" -- <see cref="_winEventProcHandle"/> below is that GCHandle, allocated in
/// <see cref="SetHook"/> and freed only in <see cref="Unhook"/>, so the delegate is rooted for the
/// hook's ENTIRE lifetime and never a moment less.
///
/// Win32 constants below were verified against live Microsoft Learn documentation (not memory) at
/// implementation time: EVENT_SYSTEM_FOREGROUND = 0x0003 ("Event Constants (Winuser.h)",
/// learn.microsoft.com/windows/win32/winauto/event-constants), WINEVENT_OUTOFCONTEXT = 0x0 (winuser.h
/// WINEVENT_* flag constants, cross-referenced against the SetWinEventHook reference page's own dwFlags
/// description).
///
/// WINEVENT_CALLBACK_CONTRACT: <see cref="WinEventProc"/> does the absolute minimum -- filters out
/// (silently, never an error) any event whose <c>eventType</c> is not exactly
/// <see cref="EVENT_SYSTEM_FOREGROUND"/> or whose <c>hwnd</c> is <see cref="IntPtr.Zero"/> (see
/// <see cref="IsAcceptableForegroundEvent"/>, a pure, independently unit-testable predicate -- the
/// filtering logic itself needs no real hook/window/thread to verify), then does nothing but a single
/// bounded, reentrancy-safe <c>PostMessageW</c> call back to the owner window it was given at
/// <see cref="SetHook"/> time -- never resolving PID/process name, never touching UI Automation or the
/// clipboard, never invoking any managed event/delegate belonging to a caller. Per Microsoft's own
/// documented reentrancy warning ("While a hook function processes an event, additional events may be
/// triggered, which may cause the hook function to reenter before the processing for the original event
/// is finished."), this single-PostMessageW body touches no shared mutable state and never blocks, so
/// reentrant invocation is inherently safe -- there is no nested work for a reentrant call to race
/// against.
/// </summary>
internal sealed class Win32ForegroundChangeNative : IForegroundChangeNative
{
    private const uint WM_APP = 0x8000;
    private const uint ShutdownMessageId = WM_APP + 1;
    private const uint ForegroundChangedMessageId = WM_APP + 2;

    // Verified against live Microsoft Learn "Event Constants (Winuser.h)" documentation.
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    // Verified against live Microsoft documentation of the WINEVENT_* flag constants (winuser.h).
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private static readonly nint HwndMessage = new(-3);

    // Kept alive as an instance field for this object's entire lifetime -- a WNDPROC delegate handed to
    // RegisterClassExW must never become eligible for GC while the window class remains registered
    // (same WndProc-delegate-lifetime finding Win32ClipboardMonitorNative/Win32SessionLockNative already
    // document).
    private readonly NativeMethods.WndProc _wndProc;

    // MICROSOFT_LEARN_CONTRACT #4 (GCHandle rooting): the WinEventProc delegate and its GCHandle are
    // allocated together in SetHook and freed together in Unhook -- rooted for exactly the hook's own
    // lifetime, never longer, never shorter. Both fields are set/cleared only from the single owner
    // thread that calls SetHook/Unhook (see ForegroundChangeMonitor's own MICROSOFT_LEARN_CONTRACT doc),
    // so no synchronization is needed here.
    private NativeMethods.WinEventDelegate? _winEventProc;
    private GCHandle _winEventProcHandle;
    private nint _hookHandle;
    private nint _ownerHwnd;

    public Win32ForegroundChangeNative()
    {
        _wndProc = DefaultWndProc;
    }

    // This window never needs custom per-message behavior -- ForegroundChangedMessageId and the
    // shutdown message are both classified directly from the MSG the owner thread's GetMessageW loop
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
        // HWND_MESSAGE -- same message-only-window rationale as Win32ClipboardMonitorNative's/
        // Win32SessionLockNative's own LISTENER_HWND_STRATEGY: not visible, no z-order, not
        // enumerable, receives no broadcast messages. SetWinEventHook's own "client thread ... must
        // have a message loop" requirement is about the calling thread's queue, not about a specific
        // HWND being registered with the hook -- this window exists so this monitor's own custom
        // ShutdownMessageId/ForegroundChangedMessageId messages have a concrete target to be posted to
        // and filtered on via GetMessageW(..., hwnd, ...), exactly mirroring the two existing
        // precedents' identical use of a message-only window for that same purpose.
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

    public bool SetHook(nint hwnd, out int win32Error)
    {
        _ownerHwnd = hwnd;
        _winEventProc = WinEventProc;
        // MICROSOFT_LEARN_CONTRACT #4: GCHandle.Alloc roots the delegate for the GC's purposes --
        // Microsoft's own guidance ("use the GCHandle structure ... this tells the garbage collector
        // not to move the callback") is followed literally here.
        _winEventProcHandle = GCHandle.Alloc(_winEventProc);

        nint hook = NativeMethods.SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        if (hook == 0)
        {
            // NOTE: Microsoft's SetWinEventHook documentation does not explicitly document a
            // GetLastError contract on failure the way RegisterClassExW/CreateWindowExW do -- this
            // captured value is therefore best-effort diagnostic information, not an officially
            // guaranteed failure code (recorded honestly here rather than assumed authoritative).
            win32Error = Marshal.GetLastWin32Error();
            _winEventProcHandle.Free();
            _winEventProc = null;
            return false;
        }

        _hookHandle = hook;
        win32Error = 0;
        return true;
    }

    public bool Unhook(out int win32Error)
    {
        bool ok = NativeMethods.UnhookWinEvent(_hookHandle);
        win32Error = ok ? 0 : Marshal.GetLastWin32Error();

        // Freed regardless of UnhookWinEvent's own return value -- once this owner thread's teardown
        // reaches here, the hook is being torn down either way (matching every other cleanup step in
        // this codebase's OwnerThreadMain "best-effort, unconditional" convention); holding the
        // delegate rooted any longer than this would only risk it outliving the object that owns it.
        if (_winEventProcHandle.IsAllocated) _winEventProcHandle.Free();
        _winEventProc = null;
        _hookHandle = 0;
        _ownerHwnd = 0;

        return ok;
    }

    public bool PostShutdown(nint hwnd) =>
        NativeMethods.PostMessageW(hwnd, ShutdownMessageId, IntPtr.Zero, IntPtr.Zero);

    public ForegroundChangeMessageKind WaitForNextMessage(nint hwnd)
    {
        // Same three-way GetMessageW signal handling as Win32ClipboardMonitorNative's/
        // Win32SessionLockNative's own WaitForNextMessage -- never posts WM_QUIT ourselves, so a 0/-1
        // result here only happens from an unexpected condition and is treated as Shutdown defensively.
        int result = NativeMethods.GetMessageW(out var msg, hwnd, 0, 0);
        if (result <= 0) return ForegroundChangeMessageKind.Shutdown;

        NativeMethods.TranslateMessage(ref msg);
        NativeMethods.DispatchMessageW(ref msg);

        if (msg.message == ShutdownMessageId) return ForegroundChangeMessageKind.Shutdown;
        if (msg.message == ForegroundChangedMessageId) return ForegroundChangeMessageKind.Changed;
        return ForegroundChangeMessageKind.Other;
    }

    // WINEVENT_INVALID_EVENT_FILTER (STEP56, frozen): a pure predicate, independently unit-testable
    // without any real hook/window/thread -- "wrong event type produces no event" and "invalid HWND
    // produces no event" are both fully characterized by this one function. Defense-in-depth even though
    // the hook is registered with eventMin == eventMax == EVENT_SYSTEM_FOREGROUND (so eventType should
    // already always equal it by construction) -- this codebase's established preference for explicit,
    // structural verification over trusting an implicit guarantee.
    internal static bool IsAcceptableForegroundEvent(uint eventType, nint hwnd) =>
        eventType == EVENT_SYSTEM_FOREGROUND && hwnd != 0;

    private void WinEventProc(
        nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        // Bounded, reentrancy-safe, touches no shared mutable state beyond a single PostMessageW call
        // (see this type's own WINEVENT_CALLBACK_CONTRACT doc) -- never resolves identity, never raises
        // any managed event directly, never blocks.
        if (!IsAcceptableForegroundEvent(eventType, hwnd)) return;

        NativeMethods.PostMessageW(_ownerHwnd, ForegroundChangedMessageId, IntPtr.Zero, IntPtr.Zero);
    }

    private static class NativeMethods
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void WinEventDelegate(
            nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

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

        // user32.dll -- verified signature against live Microsoft Learn documentation:
        // HWINEVENTHOOK SetWinEventHook(DWORD eventMin, DWORD eventMax, HMODULE hmodWinEventProc,
        // WINEVENTPROC pfnWinEventProc, DWORD idProcess, DWORD idThread, DWORD dwFlags). Returns a
        // non-zero HWINEVENTHOOK on success; zero on failure.
        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetWinEventHook(
            uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventDelegate pfnWinEventProc,
            uint idProcess, uint idThread, uint dwFlags);

        // "Call this function from the same thread that installed the event hook. UnhookWinEvent fails
        // if called from a thread different from the call that corresponds to SetWinEventHook." --
        // verified against live Microsoft Learn documentation.
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWinEvent(nint hWinEventHook);
    }
}
