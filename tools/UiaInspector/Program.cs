using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

// Phase 0 feasibility spike tool for PRIVON.
// Purpose: inspect whether the ChatGPT Windows app's composer can be found,
// read, and written to via UI Automation, using only the built-in .NET
// UIAutomationClient/UIAutomationTypes assemblies. No global hooks, no
// injection, no network calls. Only synthetic test strings are used.

if (args.Length == 0)
{
    Console.WriteLine("Usage: UiaInspector <command>");
    Console.WriteLine("  windows        - list all top-level windows belonging to ChatGPT.exe processes");
    Console.WriteLine("  dump [depth]   - dump the UIA tree of the first ChatGPT window (default depth 4)");
    Console.WriteLine("  find-composer  - heuristic search for an editable text control (the composer)");
    Console.WriteLine("  io             - write a synthetic test string into the composer and read it back");
    return;
}

switch (args[0])
{
    case "windows":
        Spikes.ListWindows();
        break;
    case "dump":
        Spikes.DumpTree(args.Length > 1 ? int.Parse(args[1]) : 4);
        break;
    case "find-composer":
        Spikes.FindComposer();
        break;
    case "io":
        Spikes.TestReadWrite();
        break;
    case "io-sendinput":
        Spikes.TestReadWriteViaSendInput();
        break;
    case "io-click-sendinput":
        Spikes.TestReadWriteViaClickThenSendInput();
        break;
    case "clipboard-spike":
        {
            int seconds = args.Length > 1 ? int.Parse(args[1]) : 45;
            var staThread = new Thread(() => Spikes.RunClipboardFallbackSpike(seconds));
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();
        }
        break;
    case "check-composer":
        Spikes.CheckComposerAgainstKnownStrings();
        break;
    case "find-send-button":
        Spikes.FindSendButton();
        break;
    case "focus-diagnostic":
        Spikes.RunFocusDiagnostic(
            args.Length > 1 ? int.Parse(args[1]) : 5,
            args.Length > 2 ? int.Parse(args[2]) : 800);
        break;
    case "enter-hotkey-spike":
        {
            int seconds = args.Length > 1 ? int.Parse(args[1]) : 150;
            var staThread = new Thread(() => Spikes.RunEnterHotkeySpike(seconds));
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();
        }
        break;
    case "enter-hotkey-spike-v2":
        Spikes.RunEnterHotkeySpikeV2(args.Length > 1 ? int.Parse(args[1]) : 240);
        break;
    case "send-button-shield-spike":
        Spikes.RunSendButtonShieldSpike(
            args.Length > 1 ? int.Parse(args[1]) : 100,
            args.Length > 2 ? int.Parse(args[2]) : 40);
        break;
    case "native-shield-step1":
        NativeShield.RunStep1(args.Length > 1 ? int.Parse(args[1]) : 40);
        break;
    case "native-shield-step2":
        NativeShield.RunStep2(args.Length > 1 ? int.Parse(args[1]) : 90);
        break;
    case "combined-stress-test":
        CombinedStressTest.Run(
            args.Length > 1 ? int.Parse(args[1]) : 340,
            args.Length > 2 ? int.Parse(args[2]) : 40);
        break;
    default:
        Console.WriteLine($"Unknown command: {args[0]}");
        break;
}

static class Spikes
{
    public static List<(AutomationElement Element, Process Proc)> GetChatGptWindows()
    {
        var procs = Process.GetProcessesByName("ChatGPT");
        var pids = new HashSet<int>(procs.Select(p => p.Id));

        var result = new List<(AutomationElement, Process)>();
        var children = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement child in children)
        {
            int pid;
            try { pid = child.Current.ProcessId; } catch { continue; }
            if (pids.Contains(pid))
            {
                var proc = procs.First(p => p.Id == pid);
                result.Add((child, proc));
            }
        }
        return result;
    }

    public static void ListWindows()
    {
        var windows = GetChatGptWindows();
        Console.WriteLine($"Found {windows.Count} top-level window(s) belonging to ChatGPT.exe:");
        int i = 0;
        foreach (var (el, proc) in windows)
        {
            i++;
            var c = el.Current;
            Console.WriteLine($"[{i}] pid={proc.Id} name=\"{c.Name}\" boundingBox={c.BoundingRectangle} isOffscreen={c.IsOffscreen}");
        }
    }

    public static void DumpTree(int maxDepth)
    {
        var windows = GetChatGptWindows();
        if (windows.Count == 0)
        {
            Console.WriteLine("No ChatGPT window found. Is the app running and visible?");
            return;
        }
        var (root, proc) = windows[0];
        Console.WriteLine($"Dumping tree for pid={proc.Id} name=\"{root.Current.Name}\" (max depth {maxDepth})");
        DumpNode(root, 0, maxDepth, 0, new int[] { 0 });
    }

    static void DumpNode(AutomationElement el, int depth, int maxDepth, int siblingIndex, int[] visitedCount)
    {
        if (depth > maxDepth) return;
        if (visitedCount[0] > 4000)
        {
            if (visitedCount[0] == 4001)
            {
                Console.WriteLine("... truncated (visited node cap reached) ...");
                visitedCount[0]++;
            }
            return;
        }
        visitedCount[0]++;

        var c = el.Current;
        string indent = new string(' ', depth * 2);
        var patterns = GetSupportedPatternNames(el);
        Console.WriteLine($"{indent}[{c.ControlType.ProgrammaticName.Replace("ControlType.", "")}] name=\"{Trunc(c.Name, 60)}\" automationId=\"{c.AutomationId}\" className=\"{c.ClassName}\" focusable={c.IsKeyboardFocusable} patterns=[{string.Join(",", patterns)}]");

        AutomationElement? child;
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            child = walker.GetFirstChild(el);
        }
        catch
        {
            return;
        }

        while (child != null)
        {
            DumpNode(child, depth + 1, maxDepth, 0, visitedCount);
            try
            {
                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            }
            catch
            {
                break;
            }
        }
    }

    static string[] GetSupportedPatternNames(AutomationElement el)
    {
        var names = new List<string>();
        try
        {
            foreach (var pattern in el.GetSupportedPatterns())
            {
                names.Add(pattern.ProgrammaticName.Replace("PatternIdentifiers.Pattern", ""));
            }
        }
        catch { }
        return names.ToArray();
    }

    static string Trunc(string s, int len) => string.IsNullOrEmpty(s) ? "" : (s.Length <= len ? s : s.Substring(0, len) + "...");

    public static void FindComposer()
    {
        var windows = GetChatGptWindows();
        if (windows.Count == 0)
        {
            Console.WriteLine("No ChatGPT window found.");
            return;
        }
        var (root, proc) = windows[0];
        Console.WriteLine($"Searching pid={proc.Id} for editable/text-pattern candidates...");

        var candidates = new List<AutomationElement>();
        CollectCandidates(root, candidates, new int[] { 0 });

        Console.WriteLine($"Found {candidates.Count} candidate(s):");
        int i = 0;
        foreach (var cand in candidates)
        {
            i++;
            var c = cand.Current;
            bool hasValue = cand.TryGetCurrentPattern(ValuePattern.Pattern, out _);
            bool hasText = cand.TryGetCurrentPattern(TextPattern.Pattern, out _);
            Console.WriteLine($"[{i}] name=\"{Trunc(c.Name, 40)}\" controlType={c.ControlType.ProgrammaticName} automationId=\"{c.AutomationId}\" hasValuePattern={hasValue} hasTextPattern={hasText} isKeyboardFocusable={c.IsKeyboardFocusable} rect={c.BoundingRectangle}");
        }
    }

    static void CollectCandidates(AutomationElement el, List<AutomationElement> results, int[] visited)
    {
        if (visited[0] > 6000) return;
        visited[0]++;

        try
        {
            var c = el.Current;
            bool isEditLike = c.ControlType == ControlType.Edit || c.ControlType == ControlType.Document;
            bool hasValue = el.TryGetCurrentPattern(ValuePattern.Pattern, out _);
            bool hasText = el.TryGetCurrentPattern(TextPattern.Pattern, out _);
            if ((isEditLike || hasValue || hasText) && c.IsKeyboardFocusable)
            {
                results.Add(el);
            }
        }
        catch (ElementNotAvailableException)
        {
            // Element became stale mid-walk (e.g. app navigated/re-rendered). Skip it.
            return;
        }

        AutomationElement? child;
        try { child = TreeWalker.ControlViewWalker.GetFirstChild(el); }
        catch (ElementNotAvailableException) { return; }
        catch { return; }

        while (child != null)
        {
            CollectCandidates(child, results, visited);
            try { child = TreeWalker.ControlViewWalker.GetNextSibling(child); }
            catch (ElementNotAvailableException) { break; }
            catch { break; }
        }
    }

    public static void TestReadWrite()
    {
        var windows = GetChatGptWindows();
        if (windows.Count == 0)
        {
            Console.WriteLine("No ChatGPT window found.");
            return;
        }
        var (root, proc) = windows[0];
        var candidates = new List<AutomationElement>();
        CollectCandidates(root, candidates, new int[] { 0 });

        // Only ControlType.Edit is eligible as a write target. This deliberately excludes
        // ControlType.Document (e.g. the RootWebArea, which mirrors the entire conversation
        // and must never be written to or bulk-read). The composer in this app surfaced as
        // an Edit control (ProseMirror), confirmed via `find-composer`.
        var ordered = candidates
            .Where(c => c.Current.ControlType == ControlType.Edit && c.Current.BoundingRectangle.Height > 0)
            .OrderByDescending(c => c.Current.BoundingRectangle.Bottom)
            .ToList();

        if (ordered.Count == 0)
        {
            Console.WriteLine("No ControlType.Edit candidate found. Refusing to guess a Document-type target.");
            return;
        }

        var target = ordered[0];
        Console.WriteLine($"Target candidate: name=\"{Trunc(target.Current.Name, 40)}\" controlType={target.Current.ControlType.ProgrammaticName} rect={target.Current.BoundingRectangle}");

        var sw = Stopwatch.StartNew();
        string before;
        try
        {
            before = ReadText(target);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"READ FAILED: {ex.Message}");
            return;
        }
        sw.Stop();
        Console.WriteLine($"Read before (len={before.Length}) in {sw.ElapsedMilliseconds}ms");

        // Synthetic-only test values.
        string testValue = "홍길동 010-0000-0000 test@example.com\n둘째 줄 🙂";

        sw.Restart();
        bool wrote = TryWrite(target, testValue);
        sw.Stop();
        Console.WriteLine($"Write attempted={wrote} in {sw.ElapsedMilliseconds}ms");

        if (!wrote)
        {
            Console.WriteLine("WRITE FAILED: no supported write path (ValuePattern.SetValue unavailable).");
            return;
        }

        sw.Restart();
        string after;
        try
        {
            after = ReadText(target);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"READ-BACK FAILED: {ex.Message}");
            return;
        }
        sw.Stop();
        Console.WriteLine($"Read-back (len={after.Length}) in {sw.ElapsedMilliseconds}ms");

        bool match = after == testValue;
        Console.WriteLine(match ? "MATCH: read-back equals written value." : "MISMATCH: read-back differs from written value.");
        if (!match)
        {
            Console.WriteLine($"  expectedLen={testValue.Length} actualLen={after.Length}");
        }

        // Restore original content so the user's window isn't left with test data.
        TryWrite(target, before);
    }

    static string ReadText(AutomationElement el)
    {
        if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vPatternObj))
        {
            return ((ValuePattern)vPatternObj).Current.Value ?? "";
        }
        if (el.TryGetCurrentPattern(TextPattern.Pattern, out var tPatternObj))
        {
            return ((TextPattern)tPatternObj).DocumentRange.GetText(-1) ?? "";
        }
        throw new InvalidOperationException("Element supports neither ValuePattern nor TextPattern.");
    }

    static bool TryWrite(AutomationElement el, string value)
    {
        if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vPatternObj))
        {
            try
            {
                ((ValuePattern)vPatternObj).SetValue(value);
                return true;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    // Synthetic-input write path: focuses the composer via UI Automation (SetFocus),
    // then simulates keystrokes with SendInput targeted at whichever control currently
    // has focus. This is standard accessibility/automation input simulation (the same
    // mechanism screen readers and RPA tools use) -- it is NOT a global keyboard hook
    // (it does not observe or intercept the user's real input) and NOT process injection
    // (no code or thread is placed into the ChatGPT process). Used here only to test
    // whether a write path exists at all; not wired into any production code path.
    public static void TestReadWriteViaSendInput()
    {
        var windows = GetChatGptWindows();
        if (windows.Count == 0)
        {
            Console.WriteLine("No ChatGPT window found.");
            return;
        }
        var (root, proc) = windows[0];
        var candidates = new List<AutomationElement>();
        CollectCandidates(root, candidates, new int[] { 0 });

        var ordered = candidates
            .Where(c => c.Current.ControlType == ControlType.Edit && c.Current.BoundingRectangle.Height > 0)
            .OrderByDescending(c => c.Current.BoundingRectangle.Bottom)
            .ToList();

        if (ordered.Count == 0)
        {
            Console.WriteLine("No ControlType.Edit candidate found.");
            return;
        }

        var target = ordered[0];
        Console.WriteLine($"Target candidate: name=\"{Trunc(target.Current.Name, 40)}\" rect={target.Current.BoundingRectangle}");

        string before;
        try { before = ReadText(target); }
        catch (Exception ex) { Console.WriteLine($"READ FAILED: {ex.Message}"); return; }
        Console.WriteLine($"Read before (len={before.Length})");

        try
        {
            target.SetFocus();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SetFocus FAILED: {ex.Message}");
            return;
        }
        Thread.Sleep(150);

        string testValue = "홍길동 010-0000-0000 test@example.com\n둘째 줄 🙂";

        var sw = Stopwatch.StartNew();
        SendCtrlA();
        Thread.Sleep(30);
        SendUnicodeText(testValue);
        Thread.Sleep(150);
        sw.Stop();
        Console.WriteLine($"SendInput sequence completed in {sw.ElapsedMilliseconds}ms");

        string after;
        try { after = ReadText(target); }
        catch (Exception ex) { Console.WriteLine($"READ-BACK FAILED: {ex.Message}"); return; }
        Console.WriteLine($"Read-back (len={after.Length})");

        bool match = after == testValue;
        Console.WriteLine(match ? "MATCH: read-back equals written value." : "MISMATCH: read-back differs from written value.");
        if (!match)
        {
            Console.WriteLine($"  expectedLen={testValue.Length} actualLen={after.Length}");
        }

        // Restore original content via the same synthetic-input path.
        SendCtrlA();
        Thread.Sleep(30);
        if (before.Length > 0)
        {
            SendUnicodeText(before);
        }
        else
        {
            SendKey(VK_DELETE);
        }
        Thread.Sleep(100);
    }

    // Click-then-type variant: some contenteditable-based editors (e.g. ProseMirror)
    // only accept synthetic input after a real mouse click establishes DOM focus/caret,
    // since UI Automation's SetFocus() only sets accessibility focus, not necessarily
    // the browser's internal focused-node state. The click point is intentionally
    // constrained to inside the composer's own bounding rectangle (already known from
    // UI Automation), so the worst case is placing the caret in the composer itself.
    public static void TestReadWriteViaClickThenSendInput()
    {
        var windows = GetChatGptWindows();
        if (windows.Count == 0)
        {
            Console.WriteLine("No ChatGPT window found.");
            return;
        }
        var (root, proc) = windows[0];
        var candidates = new List<AutomationElement>();
        CollectCandidates(root, candidates, new int[] { 0 });

        var ordered = candidates
            .Where(c => c.Current.ControlType == ControlType.Edit && c.Current.BoundingRectangle.Height > 0)
            .OrderByDescending(c => c.Current.BoundingRectangle.Bottom)
            .ToList();

        if (ordered.Count == 0)
        {
            Console.WriteLine("No ControlType.Edit candidate found.");
            return;
        }

        var target = ordered[0];
        var rect = target.Current.BoundingRectangle;
        Console.WriteLine($"Target candidate: name=\"{Trunc(target.Current.Name, 40)}\" rect={rect}");

        string before;
        try { before = ReadText(target); }
        catch (Exception ex) { Console.WriteLine($"READ FAILED: {ex.Message}"); return; }
        Console.WriteLine($"Read before (len={before.Length})");

        int clickX = (int)(rect.X + rect.Width / 2);
        int clickY = (int)(rect.Y + rect.Height / 2);
        Console.WriteLine($"Clicking inside composer rect at ({clickX},{clickY})");
        SetCursorPos(clickX, clickY);
        Thread.Sleep(50);
        SendLeftClick();
        Thread.Sleep(150);

        var sw = Stopwatch.StartNew();
        SendCtrlA();
        Thread.Sleep(30);
        string testValue = "홍길동 010-0000-0000 test@example.com\n둘째 줄 🙂";
        SendUnicodeText(testValue);
        Thread.Sleep(150);
        sw.Stop();
        Console.WriteLine($"Click+SendInput sequence completed in {sw.ElapsedMilliseconds}ms");

        string after;
        try { after = ReadText(target); }
        catch (Exception ex) { Console.WriteLine($"READ-BACK FAILED: {ex.Message}"); return; }
        Console.WriteLine($"Read-back (len={after.Length})");

        bool match = after == testValue;
        Console.WriteLine(match ? "MATCH: read-back equals written value." : "MISMATCH: read-back differs from written value.");
        if (!match)
        {
            Console.WriteLine($"  expectedLen={testValue.Length} actualLen={after.Length}");
        }

        SendCtrlA();
        Thread.Sleep(30);
        if (before.Length > 0)
        {
            SendUnicodeText(before);
        }
        else
        {
            SendKey(VK_DELETE);
        }
        Thread.Sleep(100);
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetCursorPos(int X, int Y);

    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;

    static void SendLeftClick()
    {
        var down = new INPUT { type = 0 /* INPUT_MOUSE */, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } } };
        var up = new INPUT { type = 0, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } } };
        SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(30);
        SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
    }

    const ushort VK_DELETE = 0x2E;
    const ushort VK_CONTROL = 0x11;
    const ushort VK_A = 0x41;

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_UNICODE = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    static void SendKeyDownUp(ushort vk)
    {
        var inputs = new INPUT[2];
        inputs[0] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = 0 } } };
        inputs[1] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    static void SendKey(ushort vk) => SendKeyDownUp(vk);

    static void SendCtrlA()
    {
        var down = new INPUT[2];
        down[0] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = 0 } } };
        down[1] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_A, dwFlags = 0 } } };
        SendInput(2, down, Marshal.SizeOf<INPUT>());
        var up = new INPUT[2];
        up[0] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_A, dwFlags = KEYEVENTF_KEYUP } } };
        up[1] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } };
        SendInput(2, up, Marshal.SizeOf<INPUT>());
    }

    static void SendUnicodeText(string text)
    {
        // UTF-16 code units, including surrogate pairs for emoji, sent one at a time
        // via KEYEVENTF_UNICODE so no keyboard layout / VK mapping is needed.
        foreach (char ch in text)
        {
            var down = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE } } };
            var up = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } };
            SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>());
            SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
        }
    }

    // ---- Clipboard Fallback Spike (K-T) ----
    // Known synthetic values only. The raw value is never written to Console/log/file;
    // only booleans and lengths are reported for it. The protected value contains no
    // PII so it is safe to print as-is.
    static readonly string RawTestString = "홍길동 010-0000-0000 test@example.com\n둘째 줄 🙂";
    static readonly string ProtectedTestString = "[이름1] [전화번호1] [이메일1]\n둘째 줄 🙂";

    const int WM_CLIPBOARDUPDATE = 0x031D;
    const uint CF_UNICODETEXT = 13;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    static extern uint GetClipboardSequenceNumber();

    public static void RunClipboardFallbackSpike(int seconds)
    {
        var chatGptPids = new HashSet<int>(Process.GetProcessesByName("ChatGPT").Select(p => p.Id));
        if (chatGptPids.Count == 0)
        {
            Console.WriteLine("No ChatGPT process found. Start the app first.");
            return;
        }

        var window = new System.Windows.Window
        {
            Width = 0,
            Height = 0,
            WindowStyle = System.Windows.WindowStyle.None,
            ShowInTaskbar = false,
            Visibility = System.Windows.Visibility.Hidden,
        };
        var helper = new System.Windows.Interop.WindowInteropHelper(window);
        IntPtr hwnd = helper.EnsureHandle();

        bool listenerAdded = AddClipboardFormatListener(hwnd);
        Console.WriteLine($"[K] AddClipboardFormatListener registration: {(listenerAdded ? "PASS" : "FAIL")}");
        if (!listenerAdded)
        {
            Console.WriteLine($"  Win32Error={Marshal.GetLastWin32Error()}");
            return;
        }

        int eventCount = 0;
        var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
        source.AddHook((IntPtr hWndInner, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_CLIPBOARDUPDATE)
            {
                eventCount++;
                Console.WriteLine($"[event #{eventCount}] WM_CLIPBOARDUPDATE received");
                HandleClipboardChange(chatGptPids);
            }
            return IntPtr.Zero;
        });

        Console.WriteLine($"Listening for {seconds}s.");
        Console.WriteLine("Manual steps (do this now):");
        Console.WriteLine("  1) Focus the ChatGPT composer.");
        Console.WriteLine("  2) Type this synthetic test line WITHOUT pressing Enter:");
        Console.WriteLine("     홍길동 010-0000-0000 test@example.com");
        Console.WriteLine("     (then Shift+Enter, then: 둘째 줄 🙂 )");
        Console.WriteLine("  3) Select all of it (Ctrl+A) and Copy (Ctrl+C).");
        Console.WriteLine("  4) Wait a moment, then Paste (Ctrl+V) to overwrite the selection.");
        Console.WriteLine("  5) Do NOT press Enter.");

        var dispatcherTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        dispatcherTimer.Tick += (s, e) =>
        {
            dispatcherTimer.Stop();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
        };
        dispatcherTimer.Start();
        System.Windows.Threading.Dispatcher.Run();

        RemoveClipboardFormatListener(hwnd);
        Console.WriteLine($"Spike ended. Total WM_CLIPBOARDUPDATE events observed: {eventCount}");
    }

    static void HandleClipboardChange(HashSet<int> chatGptPids)
    {
        var t0 = Stopwatch.GetTimestamp();

        IntPtr fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out uint fgPid);
        bool isChatGptActive = chatGptPids.Contains((int)fgPid);
        Console.WriteLine($"  [L] foregroundIsChatGpt={isChatGptActive}");
        if (!isChatGptActive)
        {
            Console.WriteLine("  -> skip: ChatGPT not the active/foreground target; clipboard left untouched");
            return;
        }

        if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
        {
            Console.WriteLine("  -> skip: no CF_UNICODETEXT present (non-text clipboard content); left untouched");
            return;
        }

        uint seq0 = GetClipboardSequenceNumber();

        bool matchesRawTest;
        int currentLen;
        {
            string? current;
            try { current = System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText); }
            catch (Exception ex) { Console.WriteLine($"  -> clipboard read failed: {ex.GetType().Name}"); return; }

            if (current == ProtectedTestString)
            {
                Console.WriteLine("  -> this is our own protected-value write echoing back; ignoring");
                return;
            }

            currentLen = current?.Length ?? 0;
            matchesRawTest = current == RawTestString;
            current = null; // drop reference as soon as the comparison is done
        }

        Console.WriteLine($"  [M] clipboard text length={currentLen}, matchesKnownSyntheticRawString={matchesRawTest}");
        if (!matchesRawTest)
        {
            Console.WriteLine("  -> not the known synthetic test string; out of spike scope, left untouched");
            return;
        }

        // Q/R: re-check the sequence number right before writing. If it moved since
        // detection, someone else changed the clipboard in the meantime -- do not overwrite.
        uint seq1 = GetClipboardSequenceNumber();
        if (seq1 != seq0)
        {
            Console.WriteLine("  [Q/R] clipboard sequence changed since detection; aborting write, not overwriting");
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(ProtectedTestString, System.Windows.TextDataFormat.UnicodeText);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  -> protected write FAILED: {ex.GetType().Name}");
            return;
        }

        string? after;
        try { after = System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText); }
        catch (Exception ex) { Console.WriteLine($"  -> read-back FAILED: {ex.GetType().Name}"); return; }

        bool verified = after == ProtectedTestString;
        after = null;

        var t1 = Stopwatch.GetTimestamp();
        double ms = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
        Console.WriteLine($"  [N] protected value written; read-back verified={verified}");
        Console.WriteLine($"  [T] copy-detected -> verified latency = {ms:F1}ms");
    }

    // Reads the ChatGPT composer's current value and classifies it against the two
    // known constants, without ever printing unrecognized content (which could be
    // real conversation text rather than our test strings).
    public static void CheckComposerAgainstKnownStrings()
    {
        var windows = GetChatGptWindows();
        if (windows.Count == 0)
        {
            Console.WriteLine("No ChatGPT window found.");
            return;
        }
        var (root, proc) = windows[0];
        var candidates = new List<AutomationElement>();
        CollectCandidates(root, candidates, new int[] { 0 });
        var ordered = candidates
            .Where(c => c.Current.ControlType == ControlType.Edit && c.Current.BoundingRectangle.Height > 0)
            .OrderByDescending(c => c.Current.BoundingRectangle.Bottom)
            .ToList();

        if (ordered.Count == 0)
        {
            Console.WriteLine("No ControlType.Edit candidate found.");
            return;
        }

        string current;
        try { current = ReadText(ordered[0]); }
        catch (Exception ex) { Console.WriteLine($"READ FAILED: {ex.Message}"); return; }

        string classification =
            current == ProtectedTestString ? "PROTECTED_VALUE_PRESENT" :
            current == RawTestString ? "RAW_VALUE_STILL_PRESENT" :
            current.Length == 0 ? "EMPTY" :
            "OTHER_UNRECOGNIZED_CONTENT";

        Console.WriteLine($"[O/P] composer classification={classification} (length={current.Length})");
        current = "";
    }

    // Investigation only: locate candidate send-button elements and report their
    // identification properties + InvokePattern/IsEnabled state. Does NOT call Invoke().
    public static void FindSendButton()
    {
        var windows = GetChatGptWindows();
        if (windows.Count == 0)
        {
            Console.WriteLine("No ChatGPT window found.");
            return;
        }
        var (root, proc) = windows[0];

        // Report composer text presence (length only) for correlation with button state.
        var editCandidates = new List<AutomationElement>();
        CollectCandidates(root, editCandidates, new int[] { 0 });
        var composer = editCandidates
            .Where(c => c.Current.ControlType == ControlType.Edit && c.Current.BoundingRectangle.Height > 0)
            .OrderByDescending(c => c.Current.BoundingRectangle.Bottom)
            .FirstOrDefault();

        if (composer != null)
        {
            try
            {
                string text = ReadText(composer);
                Console.WriteLine($"Composer current text length={text.Length} (content not printed; note: this app previously misreported placeholder as value when truly empty, so length alone is not fully reliable)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Composer read failed: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("Composer element not found in this pass.");
        }

        var buttons = new List<AutomationElement>();
        CollectButtons(root, buttons, new int[] { 0 });

        Console.WriteLine($"Found {buttons.Count} Button-type element(s):");
        int i = 0;
        foreach (var b in buttons)
        {
            i++;
            var c = b.Current;
            bool hasInvoke = b.TryGetCurrentPattern(InvokePattern.Pattern, out _);
            Console.WriteLine($"[{i}] name=\"{Trunc(c.Name, 50)}\" automationId=\"{c.AutomationId}\" className=\"{c.ClassName}\" isEnabled={c.IsEnabled} hasInvokePattern={hasInvoke} rect={c.BoundingRectangle}");
        }
    }

    static void CollectButtons(AutomationElement el, List<AutomationElement> results, int[] visited)
    {
        if (visited[0] > 8000) return;
        visited[0]++;

        try
        {
            if (el.Current.ControlType == ControlType.Button)
            {
                results.Add(el);
            }
        }
        catch (ElementNotAvailableException) { return; }

        AutomationElement? child;
        try { child = TreeWalker.ControlViewWalker.GetFirstChild(el); }
        catch (ElementNotAvailableException) { return; }
        catch { return; }

        while (child != null)
        {
            CollectButtons(child, results, visited);
            try { child = TreeWalker.ControlViewWalker.GetNextSibling(child); }
            catch (ElementNotAvailableException) { break; }
            catch { break; }
        }
    }

    // ---- Enter Hotkey Spike (U-AC) ----
    // Uses RegisterHotKey (a standard, sanctioned Win32 API for exactly this use case --
    // the same mechanism media-key/global-shortcut apps use) instead of any keyboard hook.
    // No WH_KEYBOARD_LL, no keylogging, no SendInput, no injection, no Invoke() on the
    // send button. The hotkey is registered/unregistered dynamically, driven by two
    // legitimate, non-hook signals: SetWinEventHook(EVENT_SYSTEM_FOREGROUND) (out-of-context,
    // no DLL injected into the target process) and UI Automation's focus-changed event.
    // A 400ms poll is added purely as a self-healing safety net against missed events,
    // since an orphaned system-wide Enter hotkey is the single biggest risk of this spike.
    const int WM_HOTKEY = 0x0312;
    const uint VK_RETURN = 0x0D;
    const uint MOD_NONE = 0x0000;
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const int HOTKEY_ID = 0xB001;

    delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    static void LogTs(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

    public static void RunEnterHotkeySpike(int seconds)
    {
        var chatGptPids = new HashSet<int>(Process.GetProcessesByName("ChatGPT").Select(p => p.Id));
        if (chatGptPids.Count == 0)
        {
            Console.WriteLine("No ChatGPT process found.");
            return;
        }

        var window = new System.Windows.Window
        {
            Width = 0,
            Height = 0,
            WindowStyle = System.Windows.WindowStyle.None,
            ShowInTaskbar = false,
            Visibility = System.Windows.Visibility.Hidden,
        };
        var helper = new System.Windows.Interop.WindowInteropHelper(window);
        IntPtr hwnd = helper.EnsureHandle();

        bool isRegistered = false;
        int hotkeyEventCount = 0;
        int reevalCount = 0;

        // PRIMARY: FocusedElement.ProcessId == ChatGPT pid AND ClassName.Contains("ProseMirror-focused")
        // (exact-match "== ProseMirror" was the bug identified by the focus-signal diagnostic --
        // the real focused className is "ProseMirror ProseMirror-focused", a two-class string).
        // SECONDARY cross-check: GetGUIThreadInfo().hwndFocus owned by a ChatGPT process.
        bool IsComposerFocused(out bool secondaryCrossCheck)
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
            if (GetGUIThreadInfo(0, ref gti))
            {
                secondaryCrossCheck = IsHwndOwnedByChatGpt(gti.hwndFocus, chatGptPids);
            }
            return primary;
        }

        void Reevaluate(string trigger)
        {
            reevalCount++;
            IntPtr fg = GetForegroundWindow();
            GetWindowThreadProcessId(fg, out uint fgPid);
            bool fgIsChatGpt = chatGptPids.Contains((int)fgPid);
            bool secondaryCrossCheck = false;
            bool composerFocusedPrimary = fgIsChatGpt && IsComposerFocused(out secondaryCrossCheck);
            bool shouldRegister = fgIsChatGpt && composerFocusedPrimary;

            if (shouldRegister && !isRegistered)
            {
                bool ok = RegisterHotKey(hwnd, HOTKEY_ID, MOD_NONE, VK_RETURN);
                int err = ok ? 0 : Marshal.GetLastWin32Error();
                isRegistered = ok;
                LogTs($"[{trigger}] REGISTER attempt: ok={ok} win32Error={err} fgIsChatGpt={fgIsChatGpt} composerFocusedPrimary={composerFocusedPrimary} secondaryCrossCheck={secondaryCrossCheck}");
            }
            else if (!shouldRegister && isRegistered)
            {
                bool ok = UnregisterHotKey(hwnd, HOTKEY_ID);
                isRegistered = false;
                LogTs($"[{trigger}] UNREGISTER: ok={ok} fgIsChatGpt={fgIsChatGpt} composerFocusedPrimary={composerFocusedPrimary} secondaryCrossCheck={secondaryCrossCheck}");
            }
            else
            {
                LogTs($"[{trigger}] no change (registered={isRegistered} fgIsChatGpt={fgIsChatGpt} composerFocusedPrimary={composerFocusedPrimary} secondaryCrossCheck={secondaryCrossCheck})");
            }
        }

        var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
        source.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                hotkeyEventCount++;
                IntPtr fg2 = GetForegroundWindow();
                GetWindowThreadProcessId(fg2, out uint fgPid2);
                bool fgIsChatGpt2 = chatGptPids.Contains((int)fgPid2);
                bool secondaryCrossCheck2 = false;
                bool composerFocused2 = fgIsChatGpt2 && IsComposerFocused(out secondaryCrossCheck2);
                LogTs($"[X/AC][hotkey #{hotkeyEventCount}] WM_HOTKEY received. Re-verified at receipt: fgIsChatGpt={fgIsChatGpt2} composerFocusedPrimary={composerFocused2} secondaryCrossCheck={secondaryCrossCheck2}");

                try
                {
                    var focused = AutomationElement.FocusedElement;
                    if (focused != null && focused.Current.ControlType == ControlType.Edit)
                    {
                        int focusedPid = focused.Current.ProcessId;
                        string focusedClass = focused.Current.ClassName ?? "";
                        LogTs($"  focusedElement pid={focusedPid} isChatGptPid={chatGptPids.Contains(focusedPid)} classNameContainsProseMirrorFocused={focusedClass.Contains("ProseMirror-focused")}");
                        string text = ReadText(focused);
                        bool stillHasTestMarker = text.Contains("PRIVON HOTKEY RETEST");
                        LogTs($"  composer length={text.Length} stillContainsTestMarker={stillHasTestMarker}");
                        text = "";
                    }
                }
                catch (Exception ex) { LogTs($"  composer re-read failed: {ex.GetType().Name}"); }
            }
            return IntPtr.Zero;
        });

        WinEventDelegate winEventProc = (IntPtr hHook, uint eventType, IntPtr hwndEvt, int idObject, int idChild, uint thread, uint time) =>
        {
            Reevaluate("EVENT_SYSTEM_FOREGROUND");
        };
        GCHandle gch = GCHandle.Alloc(winEventProc);
        IntPtr hWinEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        LogTs($"SetWinEventHook(EVENT_SYSTEM_FOREGROUND) installed: {hWinEventHook != IntPtr.Zero}");

        AutomationFocusChangedEventHandler focusHandler = (s, e) => Reevaluate("UIA_FOCUS_CHANGED");
        Automation.AddAutomationFocusChangedEventHandler(focusHandler);

        var pollTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        pollTimer.Tick += (s, e) => Reevaluate("POLL(self-heal)");
        pollTimer.Start();

        Reevaluate("INITIAL");

        Console.WriteLine($"Listening for {seconds}s. Manual steps (do them now, in order, with pauses):");
        Console.WriteLine("  1) Focus ChatGPT composer. Type: PRIVON HOTKEY RETEST   (do NOT press Enter yet)");
        Console.WriteLine("  2) Now press Enter once. [retests U, V, X]");
        Console.WriteLine("  3) Type a few more characters, then press Shift+Enter (should insert a newline, not send). [retests Y]");
        Console.WriteLine("  4) Open the chat title rename field (right-click a chat -> rename) and click into it, then press Enter there. [retests item 5]");
        Console.WriteLine("  5) Switch to a different app (e.g. Notepad/terminal) and press Enter there. [retests AA]");
        Console.WriteLine("  6) Alt+Tab back and forth between ChatGPT and the other app at least 10 times, quickly. [retests AB/item 7]");
        Console.WriteLine("  Do NOT worry about cleanup -- the tool unregisters the hotkey automatically when this ends.");

        var stopTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        stopTimer.Tick += (s, e) =>
        {
            stopTimer.Stop();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
        };
        stopTimer.Start();

        try
        {
            System.Windows.Threading.Dispatcher.Run();
        }
        finally
        {
            pollTimer.Stop();
            if (isRegistered)
            {
                bool ok = UnregisterHotKey(hwnd, HOTKEY_ID);
                LogTs($"Final cleanup UnregisterHotKey: ok={ok}");
            }
            if (hWinEventHook != IntPtr.Zero) UnhookWinEvent(hWinEventHook);
            Automation.RemoveAutomationFocusChangedEventHandler(focusHandler);
            gch.Free();
            LogTs($"Spike ended. hotkeyEventCount={hotkeyEventCount} reevalCount={reevalCount} finalRegisteredState={isRegistered}");
        }
    }

    // ---- Enter Hotkey Spike V2: lifecycle-bug fix ----
    // Root cause of the V1 leak: RegisterHotKey/UnregisterHotKey must be called from the
    // thread that owns the target hwnd. WM_HOTKEY delivery and the DispatcherTimer poll
    // both ran correctly on the hidden window's own STA thread, but .NET's
    // AutomationFocusChangedEventHandler callbacks fire on a UI-Automation-managed
    // threadpool thread, NOT the registering thread -- calling RegisterHotKey/
    // UnregisterHotKey directly from that callback failed with
    // ERROR_WINDOW_OF_OTHER_THREAD (1408), and a failed Unregister was then wrongly
    // recorded as success, orphaning the hotkey.
    //
    // Fix: a single dedicated "hotkey owner thread" is the ONLY thread that ever calls
    // RegisterHotKey/UnregisterHotKey. Every other signal source (WinEvent hook, UIA
    // focus-changed, poll) only decides desired state and posts a REGISTER_REQUEST /
    // UNREGISTER_REQUEST onto the owner thread's Dispatcher queue. The `registered` bool
    // is mutated ONLY inside the owner-thread handlers, and ONLY on a successful Win32
    // return value.
    public static void RunEnterHotkeySpikeV2(int seconds)
    {
        var chatGptPids = new HashSet<int>(Process.GetProcessesByName("ChatGPT").Select(p => p.Id));
        if (chatGptPids.Count == 0)
        {
            Console.WriteLine("No ChatGPT process found.");
            return;
        }

        IntPtr hwnd = IntPtr.Zero;
        System.Windows.Threading.Dispatcher? ownerDispatcher = null;
        int ownerThreadId = -1;
        var ownerReady = new ManualResetEventSlim(false);
        Action<string>? registerLocal = null;
        Action<string>? unregisterLocal = null;

        bool isRegistered = false;
        bool desyncDetected = false;
        int hotkeyEventCount = 0;
        int registerAttempts = 0, registerFailures = 0;
        int unregisterAttempts = 0, unregisterFailures = 0;
        int alreadyRegisteredErrors = 0;
        int wrongThreadErrors = 0;

        var ownerThread = new Thread(() =>
        {
            ownerThreadId = Thread.CurrentThread.ManagedThreadId;
            var window = new System.Windows.Window
            {
                Width = 0,
                Height = 0,
                WindowStyle = System.Windows.WindowStyle.None,
                ShowInTaskbar = false,
                Visibility = System.Windows.Visibility.Hidden,
            };
            var helper = new System.Windows.Interop.WindowInteropHelper(window);
            hwnd = helper.EnsureHandle();
            ownerDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

            void DoRegister(string trigger)
            {
                registerAttempts++;
                bool ok = RegisterHotKey(hwnd, HOTKEY_ID, MOD_NONE, VK_RETURN);
                int err = ok ? 0 : Marshal.GetLastWin32Error();
                if (ok)
                {
                    isRegistered = true;
                    LogTs($"[{trigger}][owner-thread={Thread.CurrentThread.ManagedThreadId}] REGISTER SUCCESS win32Error=0 registered=true");
                }
                else
                {
                    registerFailures++;
                    if (err == 1408) wrongThreadErrors++;
                    LogTs($"[{trigger}][owner-thread={Thread.CurrentThread.ManagedThreadId}] REGISTER FAILED win32Error={err} (state unchanged, registered={isRegistered})");
                    if (err == 1409)
                    {
                        alreadyRegisteredErrors++;
                        desyncDetected = true;
                        LogTs($"[DESYNC] ERROR_HOTKEY_ALREADY_REGISTERED -- internal registered={isRegistered} but OS disagrees. One corrective UnregisterHotKey attempt, then halting auto re-register until resolved.");
                        DoUnregisterLocal("DESYNC_RECOVERY");
                    }
                }
            }

            void DoUnregisterLocal(string trigger)
            {
                unregisterAttempts++;
                bool ok = UnregisterHotKey(hwnd, HOTKEY_ID);
                int err = ok ? 0 : Marshal.GetLastWin32Error();
                if (ok)
                {
                    isRegistered = false;
                    desyncDetected = false;
                    LogTs($"[{trigger}][owner-thread={Thread.CurrentThread.ManagedThreadId}] UNREGISTER SUCCESS win32Error=0 registered=false");
                }
                else
                {
                    unregisterFailures++;
                    if (err == 1408) wrongThreadErrors++;
                    LogTs($"[{trigger}][owner-thread={Thread.CurrentThread.ManagedThreadId}] UNREGISTER FAILED win32Error={err} (state unchanged, registered={isRegistered})");
                }
            }

            var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            source.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
                {
                    hotkeyEventCount++;
                    IntPtr fg2 = GetForegroundWindow();
                    GetWindowThreadProcessId(fg2, out uint fgPid2);
                    bool fgIsChatGpt2 = chatGptPids.Contains((int)fgPid2);
                    bool secondary2 = false;
                    bool composerFocused2 = fgIsChatGpt2 && IsComposerFocusedV2(chatGptPids, out secondary2);
                    LogTs($"[B/AC][hotkey #{hotkeyEventCount}] WM_HOTKEY received. Re-verified: fgIsChatGpt={fgIsChatGpt2} composerFocusedPrimary={composerFocused2} secondaryCrossCheck={secondary2}");
                    try
                    {
                        var focused = AutomationElement.FocusedElement;
                        if (focused != null && focused.Current.ControlType == ControlType.Edit)
                        {
                            string text = ReadText(focused);
                            bool stillHasTestMarker = text.Contains("PRIVON SHUTDOWN TEST");
                            LogTs($"  composer length={text.Length} stillContainsTestMarker={stillHasTestMarker}");
                            text = "";
                        }
                    }
                    catch (Exception ex) { LogTs($"  composer re-read failed: {ex.GetType().Name}"); }
                }
                return IntPtr.Zero;
            });

            // Exposed to outer scope via closures below (RequestRegister/RequestUnregister use these).
            registerLocal = DoRegister;
            unregisterLocal = DoUnregisterLocal;

            ownerReady.Set();
            System.Windows.Threading.Dispatcher.Run();
            LogTs($"[SHUTDOWN][owner-thread={Thread.CurrentThread.ManagedThreadId}] owner thread exiting (message loop returned)");
        });
        ownerThread.SetApartmentState(ApartmentState.STA);
        ownerThread.Start();
        ownerReady.Wait();

        void RequestRegister(string trigger)
        {
            ownerDispatcher!.BeginInvoke(new Action(() =>
            {
                if (desyncDetected) { LogTs($"[{trigger}] REGISTER_REQUEST skipped -- desync flag set"); return; }
                if (!isRegistered) registerLocal!(trigger);
            }));
        }
        void RequestUnregister(string trigger)
        {
            ownerDispatcher!.BeginInvoke(new Action(() =>
            {
                if (isRegistered) unregisterLocal!(trigger);
            }));
        }

        void Reevaluate(string trigger)
        {
            IntPtr fg = GetForegroundWindow();
            GetWindowThreadProcessId(fg, out uint fgPid);
            bool fgIsChatGpt = chatGptPids.Contains((int)fgPid);
            bool composerFocusedPrimary = fgIsChatGpt && IsComposerFocusedV2(chatGptPids, out bool secondary);
            bool shouldRegister = fgIsChatGpt && composerFocusedPrimary;
            if (shouldRegister) RequestRegister(trigger);
            else RequestUnregister(trigger);
        }

        WinEventDelegate winEventProc = (IntPtr hHook, uint eventType, IntPtr hwndEvt, int idObject, int idChild, uint thread, uint time) =>
        {
            Reevaluate("EVENT_SYSTEM_FOREGROUND"); // may run on caller thread; only posts requests, never calls Register/UnregisterHotKey directly
        };
        GCHandle gch = GCHandle.Alloc(winEventProc);
        IntPtr hWinEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        LogTs($"SetWinEventHook installed: {hWinEventHook != IntPtr.Zero}");

        AutomationFocusChangedEventHandler focusHandler = (s, e) => Reevaluate("UIA_FOCUS_CHANGED"); // known to run on a UIA threadpool thread; only posts requests
        Automation.AddAutomationFocusChangedEventHandler(focusHandler);

        var pollTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal, ownerDispatcher);
        pollTimer.Interval = TimeSpan.FromMilliseconds(400);
        pollTimer.Tick += (s, e) => Reevaluate("POLL(self-heal)");
        ownerDispatcher!.Invoke(() => pollTimer.Start());

        ownerDispatcher.Invoke(() => Reevaluate("INITIAL"));

        Console.WriteLine($"Listening for {seconds}s. Manual steps:");
        Console.WriteLine("  A) Focus ChatGPT composer (hotkey should register).");
        Console.WriteLine("  B) Type: PRIVON SHUTDOWN TEST   then press Enter once (should NOT send).");
        Console.WriteLine("  C) Switch to a different app (e.g. Notepad), press Enter there (should behave normally).");
        Console.WriteLine("  D) Switch back to ChatGPT composer (hotkey should re-register).");
        Console.WriteLine($"  E) IMPORTANT: stay with composer focused (registered=true) until the {seconds}s timer ends,");
        Console.WriteLine("     so shutdown is tested while the hotkey is actually registered.");
        Console.WriteLine("  After exit, test Enter in both ChatGPT and another app once more.");

        var stopTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal, ownerDispatcher);
        stopTimer.Interval = TimeSpan.FromSeconds(seconds);
        stopTimer.Tick += (s, e) =>
        {
            // Entire handler runs on the owner thread's Dispatcher (bound above), so steps
            // 2-7 all happen on the one thread that owns the hotkey -- no marshaling needed.
            stopTimer.Stop();
            LogTs($"[SHUTDOWN][owner-thread={Thread.CurrentThread.ManagedThreadId}] SHUTDOWN REQUEST received");

            if (isRegistered)
            {
                LogTs($"[SHUTDOWN][owner-thread={Thread.CurrentThread.ManagedThreadId}] registered=true -> UNREGISTER attempt before dispatcher shutdown");
                unregisterLocal!("SHUTDOWN_UNREGISTER"); // logs UNREGISTER SUCCESS/FAILED + win32Error; sets registered=false only on success
                if (isRegistered)
                {
                    LogTs($"[SHUTDOWN][owner-thread={Thread.CurrentThread.ManagedThreadId}] CRITICAL: UnregisterHotKey did not succeed -- proceeding to shutdown anyway since the test tool cannot hang indefinitely, but this is a FAIL condition per the test criteria (registered was not confirmed false before shutdown)");
                }
            }
            else
            {
                LogTs($"[SHUTDOWN][owner-thread={Thread.CurrentThread.ManagedThreadId}] registered=false already -- no unregister needed");
            }

            LogTs($"[SHUTDOWN][owner-thread={Thread.CurrentThread.ManagedThreadId}] registered={isRegistered} -> dispatcher shutdown");
            ownerDispatcher!.InvokeShutdown();
        };
        ownerDispatcher.Invoke(() => stopTimer.Start());

        ownerThread.Join();

        pollTimer.Stop();
        if (hWinEventHook != IntPtr.Zero) UnhookWinEvent(hWinEventHook);
        Automation.RemoveAutomationFocusChangedEventHandler(focusHandler);

        // No further RegisterHotKey/UnregisterHotKey calls after this point -- the owner
        // thread and its message loop have already exited, and re-calling from another
        // thread or after the window is torn down is exactly the bug this fix removes.
        gch.Free();
        LogTs($"Spike V2 ended. hotkeyEventCount={hotkeyEventCount} registerAttempts={registerAttempts} registerFailures={registerFailures} unregisterAttempts={unregisterAttempts} unregisterFailures={unregisterFailures} alreadyRegisteredErrors={alreadyRegisteredErrors} wrongThreadErrors={wrongThreadErrors} finalTrackedRegisteredState={isRegistered}");
    }

    // ---- Send Button Click Shield Spike ----
    // Feasibility test only: a transparent, click-capturing topmost overlay window is kept
    // positioned exactly over the live Send button BoundingRectangle while state=UNSAFE.
    // No hook, no SendInput, no injection -- the overlay is a normal WPF window that simply
    // sits above the button and absorbs the click itself. State is switched on a fixed
    // timer schedule (UNSAFE phase, then SAFE phase) so no interactive stdin is needed
    // during the run. Reuses the already-validated GetChatGptWindows/CollectButtons helpers
    // unchanged.
    [DllImport("user32.dll")]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    public static void RunSendButtonShieldSpike(int unsafeSeconds, int safeSeconds)
    {
        try
        {
            bool dpiOk = SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
            LogTs($"SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2): ok={dpiOk}");
        }
        catch (Exception ex) { LogTs($"SetProcessDpiAwarenessContext failed: {ex.GetType().Name}"); }

        var chatGptPids = new HashSet<int>(Process.GetProcessesByName("ChatGPT").Select(p => p.Id));
        if (chatGptPids.Count == 0)
        {
            Console.WriteLine("No ChatGPT process found.");
            return;
        }

        int state = 0; // 0=SAFE, 1=UNSAFE -- mutated only on the owner thread's phase timers
        int overlayVisible = 0;
        int lookupFailureStreak = 0;
        int repositionCount = 0;
        int showCount = 0;

        var ownerThread = new Thread(() =>
        {
            var overlay = new System.Windows.Window
            {
                WindowStyle = System.Windows.WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Red, // fully opaque per-pixel -- invisibility comes from Window.Opacity below, not alpha=0, so it stays click-capturing (alpha=0 pixels are click-through by OS default; a low but nonzero constant alpha is not)
                Opacity = 0.02,
                Topmost = true,
                ShowInTaskbar = false,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                ShowActivated = false, // critical: without this, Show() makes the overlay itself the foreground window, which then fails our own "is ChatGPT foreground" check on the next poll tick and causes a hide/show flicker loop. A non-activating window still receives the click (WM_LBUTTONDOWN/UP) without stealing foreground/focus.
                Left = -10000,
                Top = -10000,
                Width = 1,
                Height = 1,
            };
            overlay.Show();
            overlay.Hide();

            void UpdateOverlay(string trigger)
            {
              try
              {
                var windows = GetChatGptWindows();
                bool fgIsChatGpt = false;
                AutomationElement? sendButton = null;
                if (windows.Count > 0)
                {
                    IntPtr fg = GetForegroundWindow();
                    GetWindowThreadProcessId(fg, out uint fgPid);
                    fgIsChatGpt = chatGptPids.Contains((int)fgPid);
                    if (fgIsChatGpt)
                    {
                        var (root, proc) = windows[0];
                        var buttons = new List<AutomationElement>();
                        CollectButtons(root, buttons, new int[] { 0 });
                        sendButton = buttons.FirstOrDefault(b => { try { return b.Current.Name == "보내기"; } catch { return false; } });
                    }
                }

                bool shouldShow = state == 1 && fgIsChatGpt && sendButton != null;

                if (!shouldShow)
                {
                    if (overlayVisible == 1)
                    {
                        overlay.Hide();
                        overlayVisible = 0;
                        LogTs($"[{trigger}] overlay HIDDEN (state={(state == 1 ? "UNSAFE" : "SAFE")} fgIsChatGpt={fgIsChatGpt} sendButtonFound={sendButton != null})");
                    }
                    if (state == 1 && fgIsChatGpt && sendButton == null)
                    {
                        lookupFailureStreak++;
                        LogTs($"[{trigger}] LIMITED: state=UNSAFE but Send button lookup failed (streak={lookupFailureStreak}) -- overlay kept HIDDEN rather than left at a stale position; protection NOT guaranteed right now");
                    }
                    else
                    {
                        lookupFailureStreak = 0;
                    }
                    return;
                }

                lookupFailureStreak = 0;
                var rect = sendButton!.Current.BoundingRectangle;
                bool moved = overlay.Left != rect.X || overlay.Top != rect.Y || overlay.Width != rect.Width || overlay.Height != rect.Height;
                overlay.Left = rect.X;
                overlay.Top = rect.Y;
                overlay.Width = rect.Width;
                overlay.Height = rect.Height;

                if (overlayVisible == 0)
                {
                    overlay.Show();
                    overlayVisible = 1;
                    showCount++;
                    LogTs($"[{trigger}] overlay SHOWN at rect={rect}");
                }
                else if (moved)
                {
                    repositionCount++;
                    LogTs($"[{trigger}] overlay REPOSITIONED to rect={rect} (repositionCount={repositionCount})");
                }
              }
              catch (ElementNotAvailableException)
              {
                  // Button reference went stale between lookup and use (e.g. mid-navigation
                  // re-render). Treat exactly like a lookup failure: hide rather than risk a
                  // stale-position overlay, and let the next poll tick re-resolve it fresh.
                  if (overlayVisible == 1)
                  {
                      overlay.Hide();
                      overlayVisible = 0;
                  }
                  lookupFailureStreak++;
                  LogTs($"[{trigger}] LIMITED: ElementNotAvailableException mid-update (streak={lookupFailureStreak}) -- overlay hidden, protection NOT guaranteed right now");
              }
            }

            var pollTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            pollTimer.Tick += (s, e) => UpdateOverlay("POLL");
            pollTimer.Start();

            LogTs($"[PHASE] state=UNSAFE for {unsafeSeconds}s");
            state = 1;
            UpdateOverlay("PHASE_START_UNSAFE");

            var phaseTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(unsafeSeconds) };
            phaseTimer.Tick += (s, e) =>
            {
                phaseTimer.Stop();
                LogTs($"[PHASE] state=SAFE for {safeSeconds}s");
                state = 0;
                UpdateOverlay("PHASE_START_SAFE");

                var endTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(safeSeconds) };
                endTimer.Tick += (s2, e2) =>
                {
                    endTimer.Stop();
                    pollTimer.Stop();
                    if (overlayVisible == 1) { overlay.Hide(); overlayVisible = 0; }
                    overlay.Close();
                    LogTs("[PHASE] ending, overlay closed, shutting down dispatcher");
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                };
                endTimer.Start();
            };
            phaseTimer.Start();

            System.Windows.Threading.Dispatcher.Run();
            LogTs("owner thread exiting");
        });
        ownerThread.SetApartmentState(ApartmentState.STA);
        ownerThread.Start();

        Console.WriteLine($"Total duration: {unsafeSeconds + safeSeconds}s (UNSAFE for first {unsafeSeconds}s, then SAFE for {safeSeconds}s).");

        ownerThread.Join();
        LogTs($"Spike ended. showCount={showCount} repositionCount={repositionCount} finalLookupFailureStreak={lookupFailureStreak}");
    }

    static bool IsComposerFocusedV2(HashSet<int> chatGptPids, out bool secondaryCrossCheck)
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
        if (GetGUIThreadInfo(0, ref gti))
        {
            secondaryCrossCheck = IsHwndOwnedByChatGpt(gti.hwndFocus, chatGptPids);
        }
        return primary;
    }

    // ---- Focus-Signal Diagnostic ----
    // Read-only investigation: no RegisterHotKey, no hooks, no SendInput, no Invoke(),
    // no composer modification. Only reads HasKeyboardFocus, FocusedElement,
    // TextPattern.GetSelection() geometry, and GetGUIThreadInfo. Never prints typed
    // text content -- only structural/geometric properties.
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    static bool IsHwndOwnedByChatGpt(IntPtr hwnd, HashSet<int> chatGptPids)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            return chatGptPids.Contains((int)pid);
        }
        catch { return false; }
    }

    static AutomationElement? FindComposerOnce(AutomationElement root)
    {
        var candidates = new List<AutomationElement>();
        CollectCandidates(root, candidates, new int[] { 0 });
        return candidates
            .Where(c => c.Current.ControlType == ControlType.Edit && c.Current.BoundingRectangle.Height > 0)
            .OrderByDescending(c => c.Current.BoundingRectangle.Bottom)
            .FirstOrDefault();
    }

    public static void RunFocusDiagnostic(int count, int intervalMs)
    {
        var chatGptPids = new HashSet<int>(Process.GetProcessesByName("ChatGPT").Select(p => p.Id));
        var windows = GetChatGptWindows();
        if (windows.Count == 0)
        {
            Console.WriteLine("No ChatGPT window found.");
            return;
        }
        var (root, proc) = windows[0];

        var composer = FindComposerOnce(root);
        if (composer == null)
        {
            Console.WriteLine("Composer not found in this pass.");
            return;
        }
        var composerRect = composer.Current.BoundingRectangle;
        Console.WriteLine($"Composer reference rect={composerRect}");

        for (int i = 1; i <= count; i++)
        {
            Console.WriteLine($"--- sample {i}/{count} ---");
            CaptureFocusSample(composer, composerRect, chatGptPids);
            if (i < count) Thread.Sleep(intervalMs);
        }
    }

    static void CaptureFocusSample(AutomationElement composer, System.Windows.Rect composerRect, HashSet<int> chatGptPids)
    {
        // 1. HasKeyboardFocus
        bool hasKbFocus = false;
        try { hasKbFocus = composer.Current.HasKeyboardFocus; }
        catch (Exception ex) { Console.WriteLine($"  HasKeyboardFocus read failed: {ex.GetType().Name}"); }

        // 2. FocusedElement (name is a UI label like a placeholder/button name, not typed content -- safe to print, truncated regardless)
        string feControlType = "(none)", feName = "", feClassName = "";
        int feProcessId = -1;
        System.Windows.Rect feRect = System.Windows.Rect.Empty;
        bool feIsComposer = false;
        try
        {
            var fe = AutomationElement.FocusedElement;
            if (fe != null)
            {
                feControlType = fe.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
                feName = Trunc(fe.Current.Name, 40);
                feClassName = fe.Current.ClassName;
                feProcessId = fe.Current.ProcessId;
                feRect = fe.Current.BoundingRectangle;
                feIsComposer = feControlType == "Edit" && feClassName == "ProseMirror";
            }
        }
        catch (Exception ex) { Console.WriteLine($"  FocusedElement read failed: {ex.GetType().Name}"); }

        // 3. TextPattern.GetSelection on composer (never print actual text)
        int rangeCount = -1;
        bool? degenerate = null;
        bool caretRectAvailable = false;
        System.Windows.Rect? caretRect = null;
        try
        {
            if (composer.TryGetCurrentPattern(TextPattern.Pattern, out var tpObj))
            {
                var tp = (TextPattern)tpObj;
                var ranges = tp.GetSelection();
                rangeCount = ranges.Length;
                if (ranges.Length > 0)
                {
                    var r = ranges[0];
                    degenerate = r.CompareEndpoints(TextPatternRangeEndpoint.Start, r, TextPatternRangeEndpoint.End) == 0;
                    var rects = r.GetBoundingRectangles();
                    if (rects != null && rects.Length > 0)
                    {
                        caretRectAvailable = true;
                        caretRect = rects[0];
                    }
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"  TextPattern.GetSelection failed: {ex.GetType().Name}"); }

        // 4. GetGUIThreadInfo (idThread=0 -> current foreground thread system-wide)
        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        bool gtiOk = GetGUIThreadInfo(0, ref gti);
        bool activeIsChatGpt = false, focusIsChatGpt = false, caretIsChatGpt = false;
        if (gtiOk)
        {
            activeIsChatGpt = IsHwndOwnedByChatGpt(gti.hwndActive, chatGptPids);
            focusIsChatGpt = IsHwndOwnedByChatGpt(gti.hwndFocus, chatGptPids);
            caretIsChatGpt = IsHwndOwnedByChatGpt(gti.hwndCaret, chatGptPids);
        }

        // 5. spatial containment
        bool feRectInsideComposer = feRect != System.Windows.Rect.Empty && composerRect.Contains(feRect);
        bool caretRectInsideComposer = caretRect.HasValue && composerRect.Contains(caretRect.Value);

        Console.WriteLine($"  [1] composer.HasKeyboardFocus={hasKbFocus}");
        Console.WriteLine($"  [2] FocusedElement: controlType={feControlType} name=\"{feName}\" className=\"{feClassName}\" pid={feProcessId} rect={feRect} isComposer={feIsComposer}");
        Console.WriteLine($"  [3] TextPattern.GetSelection: rangeCount={rangeCount} degenerate={degenerate} caretRectAvailable={caretRectAvailable} caretRect={caretRect} caretInsideComposer={caretRectInsideComposer}");
        Console.WriteLine($"  [4] GUIThreadInfo: ok={gtiOk} hwndActiveIsChatGpt={activeIsChatGpt} hwndFocusIsChatGpt={focusIsChatGpt} hwndCaretIsChatGpt={caretIsChatGpt} rcCaret=({gti.rcCaret.Left},{gti.rcCaret.Top},{gti.rcCaret.Right},{gti.rcCaret.Bottom})");
        Console.WriteLine($"  [5] spatial: feRectInsideComposer={feRectInsideComposer}");
    }
}

// ============================================================================
// Native Win32 Send Button Shield -- fully independent of the WPF overlay spike
// in the Spikes class (RunSendButtonShieldSpike), which is NOT modified here.
// STEP 1: does a raw Win32 layered window (no WPF) actually consume the click?
// No WS_EX_TRANSPARENT, no keyboard/mouse hooks, no SendInput, no injection.
// ============================================================================
static class NativeShield
{
    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    const uint WS_POPUP = 0x80000000;
    const uint WS_EX_LAYERED = 0x00080000;
    const uint WS_EX_TOPMOST = 0x00000008;
    const uint WS_EX_TOOLWINDOW = 0x00000080;
    const uint WS_EX_NOACTIVATE = 0x08000000;
    // WS_EX_TRANSPARENT deliberately never used or declared here.
    const uint LWA_ALPHA = 0x2;
    const uint WM_NCHITTEST = 0x0084;
    const uint WM_MOUSEACTIVATE = 0x0021;
    const uint WM_LBUTTONDOWN = 0x0201;
    const uint WM_LBUTTONUP = 0x0202;
    const uint WM_DESTROY = 0x0002;
    const uint WM_TIMER = 0x0113;
    const int HTCLIENT = 1;
    const int MA_NOACTIVATE = 3;
    const int SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    static void Log(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

    // Fully independent of Spikes.GetChatGptWindows/CollectButtons -- duplicated minimally
    // here so this spike touches none of the existing (passed) code paths.
    static List<(AutomationElement Element, Process Proc)> GetChatGptWindowsLocal(HashSet<int> chatGptPids)
    {
        var procs = Process.GetProcessesByName("ChatGPT");
        var result = new List<(AutomationElement, Process)>();
        var children = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement child in children)
        {
            int pid;
            try { pid = child.Current.ProcessId; } catch { continue; }
            if (chatGptPids.Contains(pid))
            {
                var proc = procs.First(p => p.Id == pid);
                result.Add((child, proc));
            }
        }
        return result;
    }

    static void CollectButtonsLocal(AutomationElement el, List<AutomationElement> results, int[] visited)
    {
        if (visited[0] > 8000) return;
        visited[0]++;
        try
        {
            if (el.Current.ControlType == ControlType.Button) results.Add(el);
        }
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

    static bool IsHwndOwnedByChatGptLocal(IntPtr hwnd, HashSet<int> chatGptPids)
    {
        if (hwnd == IntPtr.Zero) return false;
        try { GetWindowThreadProcessId(hwnd, out uint pid); return chatGptPids.Contains((int)pid); }
        catch { return false; }
    }

    static AutomationElement? FindSendButtonOnce(HashSet<int> chatGptPids)
    {
        var windows = GetChatGptWindowsLocal(chatGptPids);
        if (windows.Count == 0) return null;
        var (root, proc) = windows[0];
        var buttons = new List<AutomationElement>();
        CollectButtonsLocal(root, buttons, new int[] { 0 });
        return buttons.FirstOrDefault(b => { try { return b.Current.Name == "보내기"; } catch { return false; } });
    }

    public static void RunStep1(int seconds)
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

        var chatGptPids = new HashSet<int>(Process.GetProcessesByName("ChatGPT").Select(p => p.Id));
        if (chatGptPids.Count == 0) { Console.WriteLine("No ChatGPT process found."); return; }

        var sendButton = FindSendButtonOnce(chatGptPids);
        if (sendButton == null) { Console.WriteLine("Send button not found (single lookup)."); return; }
        var rect = sendButton.Current.BoundingRectangle;
        Log($"Send button rect captured ONCE (fixed for the whole test): {rect}");

        int lbuttonDownCount = 0, lbuttonUpCount = 0, mouseActivateCount = 0, nchittestCount = 0;

        WndProcDelegate wndProc = (hWnd, msg, wParam, lParam) =>
        {
            switch (msg)
            {
                case WM_NCHITTEST:
                    nchittestCount++;
                    return new IntPtr(HTCLIENT); // never HTTRANSPARENT
                case WM_MOUSEACTIVATE:
                    mouseActivateCount++;
                    Log("[WM_MOUSEACTIVATE] received -> returning MA_NOACTIVATE (do not steal ChatGPT's activation)");
                    return new IntPtr(MA_NOACTIVATE);
                case WM_LBUTTONDOWN:
                    lbuttonDownCount++;
                    Log($"[WM_LBUTTONDOWN] #{lbuttonDownCount} received by shield (content never logged)");
                    return IntPtr.Zero;
                case WM_LBUTTONUP:
                    lbuttonUpCount++;
                    Log($"[WM_LBUTTONUP] #{lbuttonUpCount} received by shield");
                    return IntPtr.Zero;
                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
                default:
                    return DefWindowProc(hWnd, msg, wParam, lParam);
            }
        };

        const string className = "PrivonNativeShieldStep1";
        var hInstance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = hInstance,
            lpszClassName = className,
            hbrBackground = IntPtr.Zero,
        };
        var atom = RegisterClassEx(ref wc);
        if (atom == 0) { Log($"RegisterClassEx failed: win32Error={Marshal.GetLastWin32Error()}"); return; }

        const uint exStyle = WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        var hwnd = CreateWindowEx(exStyle, className, "PrivonShield", WS_POPUP,
            (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { Log($"CreateWindowEx failed: win32Error={Marshal.GetLastWin32Error()}"); return; }

        const byte alpha = 5; // non-zero, per the 3-10 requirement
        bool slwaOk = SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
        Log($"SetLayeredWindowAttributes: ok={slwaOk} alpha={alpha} (LWA_ALPHA, constant alpha -- not per-pixel)");

        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        Log($"Shield HWND created and shown (SW_SHOWNOACTIVATE) at fixed rect={rect}");

        LogFocusSnapshot("AFTER_SHOW", chatGptPids, hwnd);

        SetTimer(hwnd, new IntPtr(1), (uint)(seconds * 1000), IntPtr.Zero);

        Console.WriteLine($"Shield up for {seconds}s at FIXED rect={rect}. Click the Send button area 10 times now.");
        Console.WriteLine("Do NOT move or resize the ChatGPT window during this test.");

        MSG msg2;
        while (GetMessage(out msg2, IntPtr.Zero, 0, 0))
        {
            if (msg2.message == WM_TIMER)
            {
                KillTimer(hwnd, new IntPtr(1));
                DestroyWindow(hwnd);
            }
            TranslateMessage(ref msg2);
            DispatchMessage(ref msg2);
        }

        LogFocusSnapshot("AFTER_DESTROY", chatGptPids, IntPtr.Zero);
        Log($"Step1 ended. lbuttonDownCount={lbuttonDownCount} lbuttonUpCount={lbuttonUpCount} mouseActivateCount={mouseActivateCount} nchittestCount={nchittestCount}");
        GC.KeepAlive(wndProc);
    }

    static void LogFocusSnapshot(string tag, HashSet<int> chatGptPids, IntPtr shieldHwnd)
    {
        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        bool ok = GetGUIThreadInfo(0, ref gti);
        bool activeIsChatGpt = IsHwndOwnedByChatGptLocal(gti.hwndActive, chatGptPids);
        bool focusIsChatGpt = IsHwndOwnedByChatGptLocal(gti.hwndFocus, chatGptPids);
        bool activeIsShield = shieldHwnd != IntPtr.Zero && gti.hwndActive == shieldHwnd;
        bool focusIsShield = shieldHwnd != IntPtr.Zero && gti.hwndFocus == shieldHwnd;
        Log($"[{tag}] ok={ok} hwndActiveIsChatGpt={activeIsChatGpt} hwndFocusIsChatGpt={focusIsChatGpt} hwndActiveIsShield={activeIsShield} hwndFocusIsShield={focusIsShield}");
    }

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    const uint SWP_NOACTIVATE = 0x0010;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const int SW_HIDE = 0;

    // STEP 2: only run if STEP 1 was a full PASS. Adds dynamic UIA tracking (fresh lookup
    // every poll, never a cached element reference) on top of the STEP 1 native shield.
    // Never leaves the shield at a stale position: any lookup/foreground failure hides it
    // immediately and flips DirectTypingProtectionState to Limited rather than claiming
    // Protected with an unverified position.
    public static void RunStep2(int seconds)
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

        var chatGptPids = new HashSet<int>(Process.GetProcessesByName("ChatGPT").Select(p => p.Id));
        if (chatGptPids.Count == 0) { Console.WriteLine("No ChatGPT process found."); return; }

        int lbuttonDownCount = 0, showCount = 0, hideCount = 0, repositionCount = 0;
        string state = "Idle"; // Idle | Limited | Protected
        bool shown = false;
        System.Windows.Rect lastRect = default;

        var failureDurationsMs = new List<double>();
        int currentFailureStreak = 0, maxFailureStreak = 0;
        long failureStartTicks = 0;
        bool inFailure = false;

        void EndFailureEpisodeIfAny()
        {
            if (!inFailure) return;
            var elapsedMs = (Stopwatch.GetTimestamp() - failureStartTicks) * 1000.0 / Stopwatch.Frequency;
            failureDurationsMs.Add(elapsedMs);
            Log($"[lookup-failure-episode] duration={elapsedMs:F0}ms consecutivePolls={currentFailureStreak}");
            inFailure = false;
            currentFailureStreak = 0;
        }

        IntPtr hwnd = IntPtr.Zero;

        void Poll()
        {
            AutomationElement? sendButton;
            try { sendButton = FindSendButtonOnce(chatGptPids); }
            catch (ElementNotAvailableException) { sendButton = null; }

            IntPtr fg = GetForegroundWindow();
            GetWindowThreadProcessId(fg, out uint fgPid);
            bool fgIsChatGpt = chatGptPids.Contains((int)fgPid);

            if (!fgIsChatGpt)
            {
                if (state != "Idle") Log($"[STATE] {state} -> Idle (ChatGPT not foreground)");
                state = "Idle";
                if (shown) { ShowWindow(hwnd, SW_HIDE); shown = false; hideCount++; }
                EndFailureEpisodeIfAny();
                return;
            }

            if (sendButton == null)
            {
                if (!inFailure) { inFailure = true; failureStartTicks = Stopwatch.GetTimestamp(); currentFailureStreak = 0; }
                currentFailureStreak++;
                maxFailureStreak = Math.Max(maxFailureStreak, currentFailureStreak);
                if (state != "Limited") Log("[STATE] -> Limited (lookup failed while ChatGPT is foreground; NOT claiming Protected)");
                state = "Limited";
                if (shown) { ShowWindow(hwnd, SW_HIDE); shown = false; hideCount++; Log("[shield] HIDDEN (lookup failure) -- not left at stale position"); }
                return;
            }

            EndFailureEpisodeIfAny();
            var rect = sendButton.Current.BoundingRectangle;
            bool moved = rect != lastRect;
            SetWindowPos(hwnd, HWND_TOPMOST, (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height, SWP_NOACTIVATE);
            lastRect = rect;
            if (!shown) { ShowWindow(hwnd, SW_SHOWNOACTIVATE); shown = true; showCount++; Log($"[shield] SHOWN at {rect}"); }
            else if (moved) { repositionCount++; Log($"[shield] REPOSITIONED to {rect} (count={repositionCount})"); }
            if (state != "Protected") Log("[STATE] -> Protected");
            state = "Protected";
        }

        WndProcDelegate wndProc = (hWnd, msg, wParam, lParam) =>
        {
            switch (msg)
            {
                case WM_NCHITTEST:
                    return new IntPtr(HTCLIENT);
                case WM_MOUSEACTIVATE:
                    return new IntPtr(MA_NOACTIVATE);
                case WM_LBUTTONDOWN:
                    lbuttonDownCount++;
                    Log($"[WM_LBUTTONDOWN] #{lbuttonDownCount} received by shield (content never logged)");
                    return IntPtr.Zero;
                case WM_TIMER:
                    if (wParam.ToInt32() == 2) Poll();
                    else if (wParam.ToInt32() == 1) { KillTimer(hWnd, new IntPtr(1)); KillTimer(hWnd, new IntPtr(2)); DestroyWindow(hWnd); }
                    return IntPtr.Zero;
                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
                default:
                    return DefWindowProc(hWnd, msg, wParam, lParam);
            }
        };

        const string className = "PrivonNativeShieldStep2";
        var hInstance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = hInstance,
            lpszClassName = className,
            hbrBackground = IntPtr.Zero,
        };
        if (RegisterClassEx(ref wc) == 0) { Log($"RegisterClassEx failed: win32Error={Marshal.GetLastWin32Error()}"); return; }

        const uint exStyle = WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        hwnd = CreateWindowEx(exStyle, className, "PrivonShield2", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { Log($"CreateWindowEx failed: win32Error={Marshal.GetLastWin32Error()}"); return; }
        SetLayeredWindowAttributes(hwnd, 0, 5, LWA_ALPHA);

        SetTimer(hwnd, new IntPtr(2), 200, IntPtr.Zero);
        SetTimer(hwnd, new IntPtr(1), (uint)(seconds * 1000), IntPtr.Zero);

        Console.WriteLine($"Step2 tracking running for {seconds}s (200ms poll). Move/resize the window, alt-tab away and back, repeat quickly.");

        MSG msg2;
        while (GetMessage(out msg2, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg2);
            DispatchMessage(ref msg2);
        }

        EndFailureEpisodeIfAny();
        var avg = failureDurationsMs.Count > 0 ? failureDurationsMs.Average() : 0;
        var max = failureDurationsMs.Count > 0 ? failureDurationsMs.Max() : 0;
        Log($"Step2 ended. lbuttonDownCount={lbuttonDownCount} showCount={showCount} hideCount={hideCount} repositionCount={repositionCount} " +
            $"failureEpisodes={failureDurationsMs.Count} avgFailureMs={avg:F0} maxFailureMs={max:F0} maxConsecutiveFailurePolls={maxFailureStreak}");
        GC.KeepAlive(wndProc);
    }
}
