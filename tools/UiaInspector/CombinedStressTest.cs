using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

// Phase 0 FINAL COMBINED STRESS TEST.
// Integrates the two already-PASSed mechanisms (RegisterHotKey-based Enter interception,
// and the Native Win32 Send-button click shield) plus ChatGPT composer UIA reading, into
// ONE program, run on a single "owner thread" message loop. This file is fully independent
// of Spikes/NativeShield -- no existing passed code is modified. No new interception
// technique is introduced; this only combines the two structures already validated.
static class CombinedStressTest
{
    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize; public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    const uint WS_POPUP = 0x80000000;
    const uint WS_EX_LAYERED = 0x00080000;
    const uint WS_EX_TOPMOST = 0x00000008;
    const uint WS_EX_TOOLWINDOW = 0x00000080;
    const uint WS_EX_NOACTIVATE = 0x08000000;
    // WS_EX_TRANSPARENT never used.
    const uint LWA_ALPHA = 0x2;
    const uint WM_NCHITTEST = 0x0084;
    const uint WM_MOUSEACTIVATE = 0x0021;
    const uint WM_LBUTTONDOWN = 0x0201;
    const uint WM_LBUTTONUP = 0x0202;
    const uint WM_DESTROY = 0x0002;
    const uint WM_TIMER = 0x0113;
    const uint WM_HOTKEY = 0x0312;
    const uint WM_APP = 0x8000;
    const uint WM_PRIVON_REEVALUATE = WM_APP + 1;
    const int HTCLIENT = 1;
    const int MA_NOACTIVATE = 3;
    const int SW_SHOWNOACTIVATE = 4;
    const int SW_HIDE = 0;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint VK_RETURN = 0x0D;
    const uint MOD_NONE = 0x0000;
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const int HOTKEY_ID = 0xC001;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("user32.dll")] static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int nExitCode);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);
    [DllImport("user32.dll")] static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    static void Log(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

    static List<(AutomationElement Element, Process Proc)> GetChatGptWindowsLocal(HashSet<int> chatGptPids)
    {
        var procs = Process.GetProcessesByName("ChatGPT");
        var result = new List<(AutomationElement, Process)>();
        var children = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement child in children)
        {
            int pid;
            try { pid = child.Current.ProcessId; } catch { continue; }
            if (chatGptPids.Contains(pid)) result.Add((child, procs.First(p => p.Id == pid)));
        }
        return result;
    }

    static void CollectButtonsLocal(AutomationElement el, List<AutomationElement> results, int[] visited)
    {
        if (visited[0] > 8000) return;
        visited[0]++;
        try { if (el.Current.ControlType == ControlType.Button) results.Add(el); }
        catch (ElementNotAvailableException) { return; }

        AutomationElement? child;
        try { child = TreeWalker.ControlViewWalker.GetFirstChild(el); }
        catch (ElementNotAvailableException) { return; }
        catch { return; }

        while (child != null)
        {
            CollectButtonsLocal(child, results, visited);
            try { child = TreeWalker.ControlViewWalker.GetNextSibling(child); }
            catch (ElementNotAvailableException) { break; }
            catch { break; }
        }
    }

    static AutomationElement? FindSendButtonOnce(HashSet<int> chatGptPids)
    {
        var windows = GetChatGptWindowsLocal(chatGptPids);
        if (windows.Count == 0) return null;
        var (root, _) = windows[0];
        var buttons = new List<AutomationElement>();
        CollectButtonsLocal(root, buttons, new int[] { 0 });
        return buttons.FirstOrDefault(b => { try { return b.Current.Name == "보내기"; } catch { return false; } });
    }

    static bool IsHwndOwnedByChatGptLocal(IntPtr hwnd, HashSet<int> chatGptPids)
    {
        if (hwnd == IntPtr.Zero) return false;
        try { GetWindowThreadProcessId(hwnd, out uint pid); return chatGptPids.Contains((int)pid); }
        catch { return false; }
    }

    // Same PRIMARY/SECONDARY composer-focus check validated in enter-hotkey-spike-v2.
    static bool IsComposerFocused(HashSet<int> chatGptPids, out bool secondaryCrossCheck)
    {
        secondaryCrossCheck = false;
        bool primary = false;
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused != null)
            {
                bool pidMatch = chatGptPids.Contains(focused.Current.ProcessId);
                bool classMatch = (focused.Current.ClassName ?? "").Contains("ProseMirror-focused");
                primary = pidMatch && classMatch;
            }
        }
        catch { primary = false; }

        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(0, ref gti)) secondaryCrossCheck = IsHwndOwnedByChatGptLocal(gti.hwndFocus, chatGptPids);
        return primary;
    }

    static string ReadComposerTextSafe(AutomationElement el)
    {
        if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vObj)) return ((ValuePattern)vObj).Current.Value ?? "";
        if (el.TryGetCurrentPattern(TextPattern.Pattern, out var tObj)) return ((TextPattern)tObj).DocumentRange.GetText(-1) ?? "";
        return "";
    }

    public static void Run(int unsafeSeconds, int safeSeconds)
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

        var chatGptPids = new HashSet<int>(Process.GetProcessesByName("ChatGPT").Select(p => p.Id));
        if (chatGptPids.Count == 0) { Console.WriteLine("No ChatGPT process found."); return; }

        // ---- shared mutable state (all mutated ONLY from this thread's message handlers) ----
        bool unsafeMode = true;
        bool hotkeyRegistered = false;
        bool desyncDetected = false;
        int hotkeyEventCount = 0, registerAttempts = 0, registerFailures = 0, unregisterAttempts = 0, unregisterFailures = 0;
        int alreadyRegisteredErrors = 0, wrongThreadErrors = 0;
        int otherAppHotkeyLeaks = 0, otherAppClickLeaks = 0;

        bool shieldShown = false;
        int shieldShowCount = 0, shieldHideCount = 0, shieldRepositionCount = 0, shieldLbuttonDownCount = 0;
        System.Windows.Rect lastRect = default;
        string sendState = "Idle"; // Idle | Limited | Protected

        int buttonLookupAttempts = 0, buttonLookupFailures = 0;
        var failureDurationsMs = new List<double>();
        int currentFailureStreak = 0, maxFailureStreak = 0;
        long failureStartTicks = 0;
        bool inFailure = false;
        double longestFailureMs = 0;
        double totalUnprotectedExposureMs = 0;

        IntPtr controllerHwnd = IntPtr.Zero, shieldHwnd = IntPtr.Zero;

        void EndFailureEpisodeIfAny()
        {
            if (!inFailure) return;
            var elapsedMs = (Stopwatch.GetTimestamp() - failureStartTicks) * 1000.0 / Stopwatch.Frequency;
            failureDurationsMs.Add(elapsedMs);
            totalUnprotectedExposureMs += elapsedMs;
            longestFailureMs = Math.Max(longestFailureMs, elapsedMs);
            Log($"[lookup-failure-episode] duration={elapsedMs:F0}ms consecutivePolls={currentFailureStreak}");
            inFailure = false;
            currentFailureStreak = 0;
        }

        void DoRegisterHotkey(string trigger)
        {
            registerAttempts++;
            bool ok = RegisterHotKey(controllerHwnd, HOTKEY_ID, MOD_NONE, VK_RETURN);
            int err = ok ? 0 : Marshal.GetLastWin32Error();
            if (ok) { hotkeyRegistered = true; Log($"[{trigger}] HOTKEY REGISTER ok win32Error=0"); }
            else
            {
                registerFailures++;
                if (err == 1408) wrongThreadErrors++;
                Log($"[{trigger}] HOTKEY REGISTER FAILED win32Error={err} (registered stays {hotkeyRegistered})");
                if (err == 1409) { alreadyRegisteredErrors++; desyncDetected = true; DoUnregisterHotkey("DESYNC_RECOVERY"); }
            }
        }

        void DoUnregisterHotkey(string trigger)
        {
            unregisterAttempts++;
            bool ok = UnregisterHotKey(controllerHwnd, HOTKEY_ID);
            int err = ok ? 0 : Marshal.GetLastWin32Error();
            if (ok) { hotkeyRegistered = false; desyncDetected = false; Log($"[{trigger}] HOTKEY UNREGISTER ok win32Error=0"); }
            else
            {
                unregisterFailures++;
                if (err == 1408) wrongThreadErrors++;
                Log($"[{trigger}] HOTKEY UNREGISTER FAILED win32Error={err} (registered stays {hotkeyRegistered})");
            }
        }

        void Reevaluate(string trigger)
        {
            IntPtr fg = GetForegroundWindow();
            GetWindowThreadProcessId(fg, out uint fgPid);
            bool fgIsChatGpt = chatGptPids.Contains((int)fgPid);

            // ---- Enter-hotkey lifecycle (identical policy to enter-hotkey-spike-v2) ----
            bool composerFocused = fgIsChatGpt && IsComposerFocused(chatGptPids, out _);
            bool shouldRegister = unsafeMode && fgIsChatGpt && composerFocused;
            if (shouldRegister && !hotkeyRegistered && !desyncDetected) DoRegisterHotkey(trigger);
            else if (!shouldRegister && hotkeyRegistered) DoUnregisterHotkey(trigger);

            // ---- Send-button shield tracking ----
            if (!unsafeMode || !fgIsChatGpt)
            {
                if (sendState != "Idle") Log($"[{trigger}] SEND-STATE {sendState} -> Idle");
                sendState = "Idle";
                if (shieldShown) { ShowWindow(shieldHwnd, SW_HIDE); shieldShown = false; shieldHideCount++; }
                EndFailureEpisodeIfAny();
                return;
            }

            buttonLookupAttempts++;
            AutomationElement? sendButton;
            try { sendButton = FindSendButtonOnce(chatGptPids); }
            catch (ElementNotAvailableException) { sendButton = null; }

            if (sendButton == null)
            {
                buttonLookupFailures++;
                if (!inFailure) { inFailure = true; failureStartTicks = Stopwatch.GetTimestamp(); currentFailureStreak = 0; }
                currentFailureStreak++;
                maxFailureStreak = Math.Max(maxFailureStreak, currentFailureStreak);
                if (sendState != "Limited") Log($"[{trigger}] SEND-STATE -> Limited (lookup failed; NOT claiming Protected)");
                sendState = "Limited";
                if (shieldShown) { ShowWindow(shieldHwnd, SW_HIDE); shieldShown = false; shieldHideCount++; Log("[shield] HIDDEN (lookup failure)"); }
                return;
            }

            EndFailureEpisodeIfAny();
            var rect = sendButton.Current.BoundingRectangle;
            bool moved = rect != lastRect;
            SetWindowPos(shieldHwnd, HWND_TOPMOST, (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height, SWP_NOACTIVATE);
            lastRect = rect;
            if (!shieldShown) { ShowWindow(shieldHwnd, SW_SHOWNOACTIVATE); shieldShown = true; shieldShowCount++; Log($"[shield] SHOWN at {rect}"); }
            else if (moved) { shieldRepositionCount++; Log($"[shield] REPOSITIONED to {rect} (count={shieldRepositionCount})"); }
            if (sendState != "Protected") Log($"[{trigger}] SEND-STATE -> Protected");
            sendState = "Protected";
        }

        WndProcDelegate controllerProc = (hWnd, msg, wParam, lParam) =>
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                hotkeyEventCount++;
                IntPtr fg = GetForegroundWindow();
                GetWindowThreadProcessId(fg, out uint fgPid);
                bool fgIsChatGpt = chatGptPids.Contains((int)fgPid);
                if (!fgIsChatGpt) otherAppHotkeyLeaks++;
                bool secondary = false;
                bool composerFocused = fgIsChatGpt && IsComposerFocused(chatGptPids, out secondary);
                Log($"[HOTKEY #{hotkeyEventCount}] received. fgIsChatGpt={fgIsChatGpt} composerFocused={composerFocused} secondary={secondary}");
                try
                {
                    var focused = AutomationElement.FocusedElement;
                    if (focused != null && focused.Current.ControlType == ControlType.Edit)
                    {
                        var text = ReadComposerTextSafe(focused);
                        Log($"  composer length={text.Length} (content never logged)");
                    }
                }
                catch (Exception ex) { Log($"  composer re-read failed: {ex.GetType().Name}"); }
                return IntPtr.Zero;
            }
            if (msg == WM_TIMER)
            {
                if (wParam.ToInt32() == 2) Reevaluate("POLL");
                else if (wParam.ToInt32() == 1) { KillTimer(hWnd, new IntPtr(1)); KillTimer(hWnd, new IntPtr(2)); DestroyWindow(hWnd); }
                return IntPtr.Zero;
            }
            if (msg == WM_PRIVON_REEVALUATE) { Reevaluate("UIA_FOCUS_CHANGED"); return IntPtr.Zero; }
            if (msg == WM_DESTROY) { PostQuitMessage(0); return IntPtr.Zero; }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        };

        WndProcDelegate shieldProc = (hWnd, msg, wParam, lParam) =>
        {
            switch (msg)
            {
                case WM_NCHITTEST: return new IntPtr(HTCLIENT);
                case WM_MOUSEACTIVATE: return new IntPtr(MA_NOACTIVATE);
                case WM_LBUTTONDOWN:
                    shieldLbuttonDownCount++;
                    {
                        IntPtr fg = GetForegroundWindow();
                        GetWindowThreadProcessId(fg, out uint fgPid);
                        if (!chatGptPids.Contains((int)fgPid)) otherAppClickLeaks++;
                    }
                    Log($"[SHIELD-CLICK] #{shieldLbuttonDownCount} WM_LBUTTONDOWN consumed by shield (content never logged)");
                    return IntPtr.Zero;
                case WM_DESTROY: return IntPtr.Zero; // controller window owns PostQuitMessage
                default: return DefWindowProc(hWnd, msg, wParam, lParam);
            }
        };

        var hInstance = GetModuleHandle(null);

        var wcController = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(controllerProc), hInstance = hInstance, lpszClassName = "PrivonCombinedController" };
        if (RegisterClassEx(ref wcController) == 0) { Log($"RegisterClassEx(controller) failed: {Marshal.GetLastWin32Error()}"); return; }
        controllerHwnd = CreateWindowEx(0, "PrivonCombinedController", "PrivonController", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (controllerHwnd == IntPtr.Zero) { Log($"CreateWindowEx(controller) failed: {Marshal.GetLastWin32Error()}"); return; }

        var wcShield = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(shieldProc), hInstance = hInstance, lpszClassName = "PrivonCombinedShield" };
        if (RegisterClassEx(ref wcShield) == 0) { Log($"RegisterClassEx(shield) failed: {Marshal.GetLastWin32Error()}"); return; }
        const uint shieldExStyle = WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        shieldHwnd = CreateWindowEx(shieldExStyle, "PrivonCombinedShield", "PrivonShield", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (shieldHwnd == IntPtr.Zero) { Log($"CreateWindowEx(shield) failed: {Marshal.GetLastWin32Error()}"); return; }
        SetLayeredWindowAttributes(shieldHwnd, 0, 5, LWA_ALPHA);

        WinEventDelegate winEventProc = (h, ev, hw, obj, child, thread, time) => Reevaluate("EVENT_SYSTEM_FOREGROUND");
        var gch = GCHandle.Alloc(winEventProc);
        var hWinEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        Log($"SetWinEventHook installed: {hWinEventHook != IntPtr.Zero}");

        // UIA focus-changed fires on a UIA threadpool thread -- never touch RegisterHotKey/
        // shield HWND from there. Only PostMessage, which is thread-safe, back to our
        // controller window's queue. This is the exact fix validated in enter-hotkey-spike-v2.
        AutomationFocusChangedEventHandler focusHandler = (s, e) => PostMessage(controllerHwnd, WM_PRIVON_REEVALUATE, IntPtr.Zero, IntPtr.Zero);
        Automation.AddAutomationFocusChangedEventHandler(focusHandler);

        SetTimer(controllerHwnd, new IntPtr(2), 200, IntPtr.Zero);
        SetTimer(controllerHwnd, new IntPtr(1), (uint)((unsafeSeconds + safeSeconds) * 1000), IntPtr.Zero);

        var phaseSwitchTimer = new System.Threading.Timer(_ =>
        {
            unsafeMode = false;
            PostMessage(controllerHwnd, WM_PRIVON_REEVALUATE, IntPtr.Zero, IntPtr.Zero);
            Log("[PHASE] UNSAFE -> SAFE (manual protections released)");
        }, null, unsafeSeconds * 1000, System.Threading.Timeout.Infinite);

        Log("[PHASE] UNSAFE for " + unsafeSeconds + "s, then SAFE for " + safeSeconds + "s");
        Reevaluate("INITIAL");

        Console.WriteLine($"=== COMBINED STRESS TEST running. UNSAFE for {unsafeSeconds}s, then SAFE for {safeSeconds}s. ===");

        MSG msg2;
        while (GetMessage(out msg2, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg2);
            DispatchMessage(ref msg2);
        }

        phaseSwitchTimer.Dispose();
        EndFailureEpisodeIfAny();
        if (hWinEventHook != IntPtr.Zero) UnhookWinEvent(hWinEventHook);
        Automation.RemoveAutomationFocusChangedEventHandler(focusHandler);
        if (hotkeyRegistered) DoUnregisterHotkey("FINAL_SHUTDOWN_CHECK");
        DestroyWindow(shieldHwnd);
        gch.Free();

        var avg = failureDurationsMs.Count > 0 ? failureDurationsMs.Average() : 0;
        Log("=== COMBINED STRESS TEST ended ===");
        Log($"Enter: hotkeyEventCount={hotkeyEventCount} registerAttempts={registerAttempts} registerFailures={registerFailures} unregisterAttempts={unregisterAttempts} unregisterFailures={unregisterFailures} alreadyRegisteredErrors={alreadyRegisteredErrors} wrongThreadErrors={wrongThreadErrors} otherAppHotkeyLeaks={otherAppHotkeyLeaks}");
        Log($"Shield: shieldShowCount={shieldShowCount} shieldHideCount={shieldHideCount} shieldRepositionCount={shieldRepositionCount} shieldLbuttonDownCount={shieldLbuttonDownCount} otherAppClickLeaks={otherAppClickLeaks}");
        Log($"UIA lookup: attempts={buttonLookupAttempts} failures={buttonLookupFailures} episodes={failureDurationsMs.Count} avgFailureMs={avg:F0} longestFailureMs={longestFailureMs:F0} maxConsecutiveFailurePolls={maxFailureStreak} totalUnprotectedExposureMs={totalUnprotectedExposureMs:F0}");
        Log($"finalHotkeyRegistered={hotkeyRegistered}");
    }
}
