using System.Collections.Concurrent;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Deterministic, OS-free double for IClipboardMonitorNative -- lets ClipboardChangeMonitor's
// real thread/lifecycle/ordering/failure-cleanup logic run on a real background Thread without
// touching any actual Win32 window or clipboard. Message delivery is simulated via an in-memory
// queue the test drives directly (RaiseClipboardUpdate), matching WaitForNextMessage's blocking
// contract without a real GetMessage loop.
internal sealed class FakeClipboardMonitorNative : IClipboardMonitorNative
{
    private readonly BlockingCollection<ClipboardMonitorMessageKind> _messages = new();
    private readonly object _log = new();

    public bool RegisterClassResult { get; set; } = true;
    public int RegisterClassError { get; set; }
    public bool CreateWindowResult { get; set; } = true;
    public int CreateWindowError { get; set; }
    public bool AddListenerResult { get; set; } = true;
    public int AddListenerError { get; set; }
    public bool UnregisterClassResult { get; set; } = true;
    public bool DestroyWindowResult { get; set; } = true;
    public bool RemoveListenerResult { get; set; } = true;

    public uint SequenceNumber { get; set; } = 1;
    public bool UnicodeTextAvailable { get; set; }

    // Write-path test seam: opt-in override for GetClipboardSequenceNumber so a single test can
    // observe a DIFFERENT sequence across its several calls within one write (mutation-gate check,
    // post-Set writeSequence capture, verification-reopen check) -- e.g. to simulate another actor
    // changing the clipboard between our Close and our verification reopen (Superseded). Falls
    // back to the plain constant SequenceNumber above once the queue is empty (or if never set) --
    // every existing read-path test that only ever sets SequenceNumber is unaffected.
    private Queue<uint>? _sequenceQueue;
    public void QueueSequenceNumbers(params uint[] sequences) => _sequenceQueue = new Queue<uint>(sequences);

    // Timeout-testing seam: when true, PostShutdown is a no-op, simulating an owner thread that
    // never responds to a shutdown request (e.g. genuinely stuck). Tests that need the thread to
    // eventually exit anyway (to avoid leaking a permanently-blocked background thread for the
    // rest of the test run) call ForceShutdown, which bypasses this flag entirely.
    public bool IgnoreShutdown { get; set; }

    public bool PostReadWorkSignalResult { get; set; } = true;
    public int PostReadWorkSignalError { get; set; }

    public bool PostWriteWorkSignalResult { get; set; } = true;
    public int PostWriteWorkSignalError { get; set; }

    public List<string> CallLog { get; } = [];
    public int? RegisterWindowClassThreadId { get; private set; }
    public int? DestroyWindowThreadId { get; private set; }

    private void Log(string name)
    {
        lock (_log) CallLog.Add(name);
    }

    public bool RegisterWindowClass(string className, out int win32Error)
    {
        Log(nameof(RegisterWindowClass));
        RegisterWindowClassThreadId = Environment.CurrentManagedThreadId;
        win32Error = RegisterClassResult ? 0 : RegisterClassError;
        return RegisterClassResult;
    }

    public bool UnregisterWindowClass(string className, out int win32Error)
    {
        Log(nameof(UnregisterWindowClass));
        win32Error = 0;
        return UnregisterClassResult;
    }

    public bool CreateMessageOnlyWindow(string className, out nint hwnd, out int win32Error)
    {
        Log(nameof(CreateMessageOnlyWindow));
        hwnd = CreateWindowResult ? new nint(1) : 0;
        win32Error = CreateWindowResult ? 0 : CreateWindowError;
        return CreateWindowResult;
    }

    public bool DestroyWindow(nint hwnd, out int win32Error)
    {
        Log(nameof(DestroyWindow));
        DestroyWindowThreadId = Environment.CurrentManagedThreadId;
        win32Error = 0;
        return DestroyWindowResult;
    }

    public bool AddClipboardFormatListener(nint hwnd, out int win32Error)
    {
        Log(nameof(AddClipboardFormatListener));
        win32Error = AddListenerResult ? 0 : AddListenerError;
        return AddListenerResult;
    }

    public bool RemoveClipboardFormatListener(nint hwnd, out int win32Error)
    {
        Log(nameof(RemoveClipboardFormatListener));
        win32Error = 0;
        return RemoveListenerResult;
    }

    public uint GetClipboardSequenceNumber()
    {
        Log(nameof(GetClipboardSequenceNumber));
        if (_sequenceQueue is { Count: > 0 })
            return _sequenceQueue.Dequeue();
        return SequenceNumber;
    }

    public bool IsUnicodeTextAvailable()
    {
        Log(nameof(IsUnicodeTextAvailable));
        return UnicodeTextAvailable;
    }

    public bool PostShutdown(nint hwnd)
    {
        if (IgnoreShutdown) return true;
        _messages.Add(ClipboardMonitorMessageKind.Shutdown);
        return true;
    }

    public bool PostReadWorkSignal(nint hwnd, out int win32Error)
    {
        Log(nameof(PostReadWorkSignal));
        if (!PostReadWorkSignalResult)
        {
            win32Error = PostReadWorkSignalError;
            return false;
        }

        _messages.Add(ClipboardMonitorMessageKind.ReadWork);
        win32Error = 0;
        return true;
    }

    public bool PostWriteWorkSignal(nint hwnd, out int win32Error)
    {
        Log(nameof(PostWriteWorkSignal));
        if (!PostWriteWorkSignalResult)
        {
            win32Error = PostWriteWorkSignalError;
            return false;
        }

        _messages.Add(ClipboardMonitorMessageKind.WriteWork);
        win32Error = 0;
        return true;
    }

    public ClipboardMonitorMessageKind WaitForNextMessage(nint hwnd) => _messages.Take();

    public void RaiseClipboardUpdate() => _messages.Add(ClipboardMonitorMessageKind.ClipboardUpdate);

    // Bypasses IgnoreShutdown entirely -- used by tests to unstick a simulated-unresponsive
    // owner thread once the timeout/failure behavior under test has already been observed.
    public void ForceShutdown() => _messages.Add(ClipboardMonitorMessageKind.Shutdown);
}
