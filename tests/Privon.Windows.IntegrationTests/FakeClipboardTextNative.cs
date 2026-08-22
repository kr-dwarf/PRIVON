using System.Runtime.InteropServices;
using System.Text;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Deterministic, OS-free double for IClipboardTextNative. Unlike FakeClipboardMonitorNative,
// this fake backs GlobalLock with REAL unmanaged memory (via Marshal.AllocHGlobal) so
// ClipboardChangeMonitor's real Marshal.Copy call in ExecuteRead has something genuine to copy
// from -- the byte-level parsing/bounds logic under test is exercised for real, only the OS
// clipboard/window subsystem is faked out.
internal sealed class FakeClipboardTextNative : IClipboardTextNative, IDisposable
{
    private nint _allocatedBuffer;
    private byte[] _payload = [];

    public bool OpenClipboardResult { get; set; } = true;
    public int OpenClipboardError { get; set; }
    public bool GetHandleResult { get; set; } = true;
    public int GetHandleError { get; set; }
    public bool GetGlobalSizeResult { get; set; } = true;
    public int GetGlobalSizeError { get; set; }
    public bool GlobalLockResult { get; set; } = true;
    public int GlobalLockError { get; set; }
    public bool CloseClipboardResult { get; set; } = true;
    public int CloseClipboardError { get; set; }

    // Write-path (Phase 3A.4 STEP4) test seams. Each defaults to "succeed."
    public bool GlobalAllocResult { get; set; } = true;
    public int GlobalAllocError { get; set; }
    public bool GlobalFreeResult { get; set; } = true;
    public int GlobalFreeError { get; set; }
    public bool EmptyClipboardResult { get; set; } = true;
    public int EmptyClipboardError { get; set; }
    public bool SetClipboardDataResult { get; set; } = true;
    public int SetClipboardDataError { get; set; }

    // Verification-scenario test seams -- simulate what a real OS could (rarely, adversarially)
    // report on a post-write reopen, independent of what was actually just written:
    //   OverridePayloadOnSet: readback reports genuinely different (but well-formed) text.
    //   CorruptPayloadOnSet:  readback reports malformed bytes (odd length).
    // At most one is meaningful per test; OverridePayloadOnSet takes precedence if both are set.
    public string? OverridePayloadOnSet { get; set; }
    public bool CorruptPayloadOnSet { get; set; }

    // Write-path test seam: opt-in override for OpenClipboard so a single test can make the FIRST
    // call (the mutation bracket) succeed and a LATER call (the verification reopen) fail, or vice
    // versa. Falls back to the plain OpenClipboardResult/-Error pair once the queue is empty (or
    // if never set) -- every existing read-path test is unaffected.
    private Queue<(bool Result, int Error)>? _openClipboardQueue;
    public void QueueOpenClipboardResults(params (bool Result, int Error)[] results) =>
        _openClipboardQueue = new Queue<(bool, int)>(results);

    // Phase 3C STEP42 test seam: identical idea, for CloseClipboard -- lets a single test make the
    // FIRST call (the mutation bracket's own close) succeed and a LATER call (the verification
    // bracket's own close) fail, so "exact read-back match, but the verification close itself
    // fails" (a distinct failure point from every existing mutation-bracket-close test) can be
    // reproduced deterministically. Falls back to the plain CloseClipboardResult/-Error pair once
    // the queue is empty (or if never set).
    private Queue<(bool Result, int Error)>? _closeClipboardQueue;
    public void QueueCloseClipboardResults(params (bool Result, int Error)[] results) =>
        _closeClipboardQueue = new Queue<(bool, int)>(results);

    // Real unmanaged allocations created via TryGlobalAlloc, keyed by handle (== pointer, since
    // this fake's allocations never move) -> byte size, so TrySetClipboardData/TryGlobalFree can
    // operate on the genuine backing memory rather than a synthetic marker.
    private readonly Dictionary<nint, int> _allocationSizes = new();

    // Test-introspection seams for ownership/call-ordering assertions -- in particular, "a
    // successfully-Set handle is never freed" (STEP4's structural GLOBALFREE_LEAK_VISIBILITY /
    // ownership-transfer requirement).
    public List<nint> AllocatedHandles { get; } = [];
    public List<nint> FreedHandleLog { get; } = [];
    public nint? SetHandle { get; private set; }

    // GlobalUnlock test seam: three states matching the real Win32 contract exactly --
    //   Normal    (default): native GlobalUnlock reports "unlocked" (FALSE) + NO_ERROR -> success
    //   StillLocked:         native GlobalUnlock reports "still locked" (TRUE) -> also success
    //   Failed:               native GlobalUnlock reports FALSE + a real nonzero error -> failure
    public enum UnlockBehavior { Normal, StillLocked, Failed }
    public UnlockBehavior GlobalUnlockBehavior { get; set; } = UnlockBehavior.Normal;
    public int GlobalUnlockError { get; set; } = 1;

    public List<string> CallLog { get; } = [];
    public int? OpenClipboardThreadId { get; private set; }

    // Encodes text as UTF-16LE plus a trailing NUL terminator, mirroring a real CF_UNICODETEXT
    // allocation exactly.
    public void SetUnicodeTextPayload(string text) => _payload = Encoding.Unicode.GetBytes(text + '\0');

    public void SetRawPayload(byte[] bytes) => _payload = bytes;

    public bool OpenClipboard(nint hwnd, out int win32Error)
    {
        CallLog.Add(nameof(OpenClipboard));
        OpenClipboardThreadId = Environment.CurrentManagedThreadId;

        if (_openClipboardQueue is { Count: > 0 })
        {
            var (queuedResult, queuedError) = _openClipboardQueue.Dequeue();
            win32Error = queuedResult ? 0 : queuedError;
            return queuedResult;
        }

        win32Error = OpenClipboardResult ? 0 : OpenClipboardError;
        return OpenClipboardResult;
    }

    public bool CloseClipboard(out int win32Error)
    {
        CallLog.Add(nameof(CloseClipboard));

        if (_closeClipboardQueue is { Count: > 0 })
        {
            var (queuedResult, queuedError) = _closeClipboardQueue.Dequeue();
            win32Error = queuedResult ? 0 : queuedError;
            return queuedResult;
        }

        win32Error = CloseClipboardResult ? 0 : CloseClipboardError;
        return CloseClipboardResult;
    }

    public bool TryGetUnicodeTextHandle(out nint handle, out int win32Error)
    {
        CallLog.Add(nameof(TryGetUnicodeTextHandle));
        handle = GetHandleResult ? new nint(1) : 0;
        win32Error = GetHandleResult ? 0 : GetHandleError;
        return GetHandleResult;
    }

    public bool TryGetGlobalSize(nint handle, out nuint size, out int win32Error)
    {
        CallLog.Add(nameof(TryGetGlobalSize));
        size = GetGlobalSizeResult ? (nuint)_payload.Length : 0;
        win32Error = GetGlobalSizeResult ? 0 : GetGlobalSizeError;
        return GetGlobalSizeResult;
    }

    public bool TryGlobalLock(nint handle, out nint pointer, out int win32Error)
    {
        CallLog.Add(nameof(TryGlobalLock));
        if (!GlobalLockResult)
        {
            pointer = 0;
            win32Error = GlobalLockError;
            return false;
        }

        if (_allocationSizes.ContainsKey(handle))
        {
            // A genuine TryGlobalAlloc-backed allocation (write path) -- this fake's
            // AllocHGlobal-backed memory never moves, so the handle IS the pointer. Production
            // code Marshal.Copy's the replacement text's bytes directly into this real memory.
            pointer = handle;
            win32Error = 0;
            return true;
        }

        // Read-path fallback (unchanged from before write support was added): the synthetic
        // handle TryGetUnicodeTextHandle hands out is backed by _payload/_allocatedBuffer, not by
        // any TryGlobalAlloc call -- every existing read test still exercises exactly this path.
        FreeBuffer();
        _allocatedBuffer = Marshal.AllocHGlobal(Math.Max(_payload.Length, 1));
        if (_payload.Length > 0) Marshal.Copy(_payload, 0, _allocatedBuffer, _payload.Length);
        pointer = _allocatedBuffer;
        win32Error = 0;
        return true;
    }

    public bool TryGlobalUnlock(nint handle, out int win32Error)
    {
        CallLog.Add(nameof(TryGlobalUnlock));
        switch (GlobalUnlockBehavior)
        {
            case UnlockBehavior.StillLocked:
                win32Error = 0;
                return true;
            case UnlockBehavior.Failed:
                win32Error = GlobalUnlockError;
                return false;
            default:
                win32Error = 0;
                return true;
        }
    }

    private void FreeBuffer()
    {
        if (_allocatedBuffer != 0)
        {
            Marshal.FreeHGlobal(_allocatedBuffer);
            _allocatedBuffer = 0;
        }
    }

    public bool TryGlobalAlloc(nuint byteSize, out nint handle, out int win32Error)
    {
        CallLog.Add(nameof(TryGlobalAlloc));
        if (!GlobalAllocResult)
        {
            handle = 0;
            win32Error = GlobalAllocError;
            return false;
        }

        var size = (int)byteSize;
        nint ptr = Marshal.AllocHGlobal(Math.Max(size, 1));
        _allocationSizes[ptr] = size;
        AllocatedHandles.Add(ptr);
        handle = ptr;
        win32Error = 0;
        return true;
    }

    public bool TryGlobalFree(nint handle, out int win32Error)
    {
        CallLog.Add(nameof(TryGlobalFree));
        FreedHandleLog.Add(handle);

        if (!GlobalFreeResult)
        {
            win32Error = GlobalFreeError;
            return false;
        }

        if (_allocationSizes.Remove(handle))
            Marshal.FreeHGlobal(handle);

        win32Error = 0;
        return true;
    }

    public bool EmptyClipboard(out int win32Error)
    {
        CallLog.Add(nameof(EmptyClipboard));
        win32Error = EmptyClipboardResult ? 0 : EmptyClipboardError;
        return EmptyClipboardResult;
    }

    public bool TrySetClipboardData(nint handle, out int win32Error)
    {
        CallLog.Add(nameof(TrySetClipboardData));

        if (!SetClipboardDataResult)
        {
            win32Error = SetClipboardDataError;
            return false;
        }

        // Simulate "this handle's content is now the clipboard's content": copy its real
        // unmanaged bytes back into the same _payload field the existing read path (
        // TryGetUnicodeTextHandle/TryGetGlobalSize/TryGlobalLock) already serves reads from. This
        // is what lets a subsequent verification read genuinely reproduce what was written,
        // through the exact same already-tested read code path -- no separate write-side read
        // simulation is needed.
        if (OverridePayloadOnSet is not null)
        {
            _payload = Encoding.Unicode.GetBytes(OverridePayloadOnSet + '\0');
        }
        else if (_allocationSizes.TryGetValue(handle, out int size))
        {
            var bytes = new byte[size];
            Marshal.Copy(handle, bytes, 0, size);
            _payload = CorruptPayloadOnSet && bytes.Length > 0 ? bytes[..^1] : bytes;
        }

        SetHandle = handle;
        win32Error = 0;
        return true;
    }

    public void Dispose()
    {
        FreeBuffer();
        foreach (var handle in _allocationSizes.Keys.ToList())
            Marshal.FreeHGlobal(handle);
        _allocationSizes.Clear();
    }
}
