using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Privon.Windows;

/// <summary>
/// Phase 3A.4 STEP2/STEP3/STEP4 -- Clipboard Change Monitor Foundation, extended with a
/// read-only text snapshot primitive, a sequence-guarded write (STEP4), and -- as of Phase 3A.5
/// STEP4 -- a foreground-target execution guard in front of both. Owns a dedicated background
/// thread that creates a message-only Win32 window, registers it as a clipboard format listener,
/// raises <see cref="Changed"/> with metadata-only <see cref="ClipboardChangeNotification"/>
/// values, and answers guarded <see cref="ReadTextSnapshotAsync(ForegroundTargetSnapshot)"/> /
/// <see cref="WriteTextIfSequenceMatchesAsync(ForegroundTargetSnapshot, uint, string)"/> requests
/// -- all on that same owner thread. A <see cref="ClipboardWriteOutcome.Success"/> result is
/// still NOT <c>Privon.Core.ProtectionState.Verified</c> -- Verified additionally requires an
/// actual composer paste, composer read-back, and final validation (docs/release-gate.md), none
/// of which this Windows-transport layer performs or knows about. This type knows nothing about
/// PII/Protect/Alias/Risk domain concepts -- it only ever receives and returns plain text and
/// sequence numbers.
///
/// FOREGROUND_EXECUTION_GUARD (Phase 3A.5 STEP4): the public API surface is guarded-only. The
/// two public entry points below both require a <see cref="ForegroundTargetSnapshot"/> as their
/// FIRST parameter -- there is no public way to read or write clipboard content from outside
/// this assembly without one. The lower-level, target-agnostic primitives that used to be public
/// (bare <c>ReadTextSnapshotAsync()</c> / <c>WriteTextIfSequenceMatchesAsync(uint, string)</c>)
/// are now <c>internal</c> -- still directly exercised by this assembly's own STEP3/STEP3.1/
/// STEP4/STEP4.1 mechanics tests (via <c>InternalsVisibleTo</c>), but structurally unreachable
/// from <c>Privon.App</c> or anywhere else. This type still does not know "ChatGPT" or any other
/// product policy name -- the guard is pure mechanical (PID, process name) equality against
/// whatever <see cref="ForegroundTargetSnapshot"/> the caller supplies; see
/// <see cref="CheckForegroundTarget"/>.
///
/// Thread ownership (Phase 3A.4 STEP1 report's THREAD_OWNERSHIP finding, implemented here):
/// window-class registration, window creation/destruction, AddClipboardFormatListener/
/// RemoveClipboardFormatListener, and the GetMessage loop are ALL performed on one dedicated
/// owner thread -- never from the caller's thread. This is a PRIVON implementation invariant
/// for lifecycle discipline (matching the existing Phase 0 "never touch the HWND from another
/// thread" precedent already established in tools/UiaInspector), not a claim about a documented
/// Microsoft thread-affinity requirement on AddClipboardFormatListener itself.
///
/// Lifecycle is single-use and reject-on-repeat: a second call to <see cref="Start"/> on the
/// same instance always throws, regardless of whether the first call succeeded or failed (see
/// the Phase 3A.4 STEP1 report's own explicit "reject second Start" choice). <see cref="Stop"/>
/// and <see cref="Dispose"/> are both idempotent and safe to call before a successful Start, or
/// after a failed one -- but idempotent here means "safe to call repeatedly," not "guaranteed to
/// swallow a real cleanup failure." See <see cref="Dispose"/>'s own doc for the STEP2.1
/// DISPOSE_FAILURE_BEHAVIOR contract.
/// </summary>
public sealed class ClipboardChangeMonitor : IDisposable
{
    /// <summary>
    /// CLIPBOARD_MONITOR_STOP_TIMEOUT -- ratified as a 0.1 implementation contract value in the
    /// Phase 3A.4 STEP2.1 decision (not merely a conservative default anymore): the maximum time
    /// <see cref="Stop"/>/<see cref="Dispose"/> will wait for the owner thread to confirm exit
    /// after a shutdown request, before treating the shutdown as a lifecycle failure. This is
    /// exclusively a shutdown-join bound -- it has no relationship to clipboard contention
    /// retry/backoff (that remains explicitly un-owned by this layer, see the STEP1 report's
    /// CONTENTION_POLICY finding).
    /// </summary>
    internal static readonly TimeSpan StopTimeoutContractDefault = TimeSpan.FromSeconds(5);

    // BUG-006 GLOBAL_ROLLBACK_SLOT (process-wide, not per-instance): a quarantined rollback handle
    // must remain reachable even if the ClipboardChangeMonitor instance that created it is later
    // disposed and collected -- reachability here is deliberately independent of any single
    // instance's own lifetime (see TryResolveQuarantineAtShutdown's own doc). Exactly one
    // PRIVON-owned, potentially-RAW-bearing rollback allocation may exist across the ENTIRE process
    // at any time; this slot is the sole mechanism that enforces that bound. Guarded exclusively via
    // Interlocked -- see TryAdmitWrite/ResolveOrQuarantineRollbackHandle for the full protocol and
    // the memory-ordering argument for why plain reads of the handle/byteLength pair are safe only
    // immediately after a successful CompareExchange transition into/out of SlotQuarantined.
    internal const int SlotEmpty = 0;
    internal const int SlotReserved = 1;
    internal const int SlotQuarantined = 2;
    private static int s_slotState;
    private static nint s_quarantinedHandle;
    private static int s_quarantinedByteLength;

    // BUG-006 test-only introspection/reset seam. The slot is process-wide by design (see its own
    // doc above), so tests that deliberately drive it into Quarantined/Reserved must be able to
    // observe and reset it -- production code never calls any of these three members.
    internal static int GlobalRollbackSlotStateForTests => Volatile.Read(ref s_slotState);
    internal static nint QuarantinedHandleForTests => s_quarantinedHandle;
    internal static int QuarantinedByteLengthForTests => s_quarantinedByteLength;

    internal static void ResetGlobalRollbackSlotForTests()
    {
        Interlocked.Exchange(ref s_slotState, SlotEmpty);
        s_quarantinedHandle = 0;
        s_quarantinedByteLength = 0;
    }

    // BUG-006 test-only seam: forces the slot to Reserved without going through TryAdmitWrite --
    // lets a test deterministically simulate "some other actor already owns the slot" (e.g. a
    // concurrent writer) when proving that shutdown's own resolution attempt correctly backs off
    // rather than touching a quarantine it does not itself own.
    internal static void ForceReservedForTests() => Interlocked.Exchange(ref s_slotState, SlotReserved);

    // BUG-006 test-only seam: publishes a specific handle/byteLength as Quarantined directly,
    // without requiring a real catastrophic write failure to produce one -- lets tests exercise
    // write-admission/shutdown resolution against a known, controlled quarantine entry.
    internal static void ForceQuarantinedForTests(nint handle, int byteLength)
    {
        s_quarantinedHandle = handle;
        s_quarantinedByteLength = byteLength;
        Interlocked.Exchange(ref s_slotState, SlotQuarantined);
    }

    private enum SlotAdmission { Busy, ReservedFresh, ReservedForCleanup }

    private readonly IClipboardMonitorNative _native;
    private readonly IClipboardTextNative _textNative;
    private readonly IForegroundTargetSource _foregroundSource;
    private readonly TimeSpan _stopTimeout;
    private readonly string _windowClassName = "PrivonClipboardMonitor_" + Guid.NewGuid().ToString("N");
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _startupSignal = new(initialState: false);
    private readonly ConcurrentQueue<PendingRead> _pendingReads = new();
    private readonly ConcurrentQueue<PendingWrite> _pendingWrites = new();

    private bool _startCalled;
    private bool _disposed;
    private Thread? _ownerThread;
    private nint _hwnd;
    private volatile bool _running;
    private ExceptionDispatchInfo? _startupFailure;

    // SELF_WRITE_SUPPRESSION (Phase 3A.4 STEP4): only ever read/written on the owner thread
    // itself (set at the end of a fully-verified successful write inside ExecuteWrite/VerifyWrite,
    // read/cleared inside RaiseChanged -- both run exclusively on the owner thread's message
    // loop), so no lock/volatile is needed. Holds ONLY a sequence number -- never raw/protected
    // text -- per this layer's SENSITIVE_DATA_RULES.
    private uint? _lastSuccessfulWriteSequence;

    // Phase 3C STEP41.1 -- diagnostic-only local correlation counter. Owner-thread-only, same
    // no-lock rationale as _lastSuccessfulWriteSequence directly above: only ever read/written from
    // inside RaiseChanged, which itself only ever runs on this monitor's own owner thread. Never a
    // clipboard sequence number, never reset, never shared with any other component -- see
    // ClipboardMonitorDiagnosticEvent's own doc for exactly what it is for.
    private long _diagnosticEventCounter;

    // Phase 3C STEP41.2 -- a SEPARATE diagnostic-only local correlation counter for the
    // guarded-write boundary, distinct from _diagnosticEventCounter above (which belongs to the
    // native-notification boundary). Owner-thread-only for the identical reason: ExecuteWrite (and
    // therefore every method it calls) only ever runs on this monitor's own owner thread, via
    // ProcessOnePendingWrite's own message-loop dispatch. Never a clipboard sequence number, never
    // reset, never shared with any other component -- see ClipboardWriteDiagnosticEvent's own doc.
    private long _writeDiagnosticCounter;

    // ExpectedTarget is null for the internal, target-agnostic primitive (STEP3-era mechanics
    // tests) and non-null for every request that came in through a public guarded overload --
    // one queue, one item shape, no duplicated marshaling machinery (Phase 3A.5 STEP4
    // instruction's explicit "do not duplicate queues" guidance).
    private readonly record struct PendingRead(
        TaskCompletionSource<ClipboardTextReadResult> Tcs,
        ForegroundTargetSnapshot? ExpectedTarget);

    // Marshaled the same way as a pending read: caller enqueues under _gate, then posts a wakeup.
    private readonly record struct PendingWrite(
        TaskCompletionSource<ClipboardWriteResult> Tcs,
        ForegroundTargetSnapshot? ExpectedTarget,
        uint ExpectedSequence,
        string ReplacementText);

    /// <summary>
    /// CALLBACK_THREAD_CONTRACT (Phase 3A.4 STEP2.1): raised synchronously on the dedicated
    /// owner thread -- never marshaled to any other thread by this type, and never invoked via
    /// the thread pool. This means:
    ///   - the subscriber MUST return promptly -- it directly blocks this monitor's ability to
    ///     notice further clipboard changes or process a pending shutdown request for as long as
    ///     it runs;
    ///   - Detection/Storage/heavy App orchestration (or anything privacy-pipeline-related) must
    ///     never run directly inside this handler -- see CLIPBOARD_NOTIFICATION_DISPATCH_BOUNDARY
    ///     below;
    ///   - a future Privon.App integration must marshal the notification to an App-owned
    ///     queue/synchronization context immediately and return, not process it inline;
    ///   - as of STEP3, this specifically also means: NEVER call
    ///     <c>ReadTextSnapshotAsync().GetAwaiter().GetResult()</c>, <c>.Result</c>, or
    ///     <c>.Wait()</c> from inside this handler. Doing so would deadlock -- the read it is
    ///     waiting on can only be serviced by this same owner thread's message loop, which is
    ///     the very thread blocked waiting for the callback to return. This is a documented API
    ///     invariant, not something exercised by a deliberately-deadlocking production test.
    /// A subscriber exception is still caught and dropped here so it can never corrupt the owner
    /// thread's message loop or skip native resource cleanup (Phase 3A.4 STEP1 report's
    /// CONSUMER_CALLBACK_BOUNDARY finding) -- this isolates subscriber bugs only, it never hides
    /// an OS/clipboard-layer failure, and it does not address subscriber *blocking* (deliberately
    /// left as CLIPBOARD_NOTIFICATION_DISPATCH_BOUNDARY -- DEFERRED TO APP INTEGRATION; no new
    /// dispatch mechanism such as ThreadPool/Channel/SynchronizationContext is introduced here).
    /// </summary>
    public event EventHandler<ClipboardChangeNotification>? Changed;

    /// <summary>
    /// Phase 3C STEP41.1 -- diagnostic-only, metadata-only observation of the native
    /// <c>WM_CLIPBOARDUPDATE</c> -&gt; self-write-suppression-decision -&gt; <see cref="Changed"/>-delivery
    /// boundary. Raised synchronously on this monitor's own owner thread, exactly like
    /// <see cref="Changed"/> itself -- same CALLBACK_THREAD_CONTRACT (subscriber must return
    /// promptly, never do blocking/async work here), and a subscriber exception is caught and
    /// dropped identically (see <see cref="RaiseDiagnostic"/>) so a broken diagnostic observer can
    /// never affect clipboard monitoring itself. With zero subscribers (the default -- nothing in
    /// this assembly's own production code ever subscribes to its own event) this is a single
    /// null-conditional check per native notification: no behavior change, no new allocation, no
    /// new thread, no file I/O of any kind performed by this type (see
    /// <see cref="ClipboardMonitorDiagnosticEvent"/>'s own doc -- any persistence belongs entirely
    /// to an upper layer that subscribes to this event).
    /// </summary>
    public event EventHandler<ClipboardMonitorDiagnosticEvent>? DiagnosticObserved;

    /// <summary>
    /// Phase 3C STEP41.2 -- diagnostic-only, metadata-only observation of the guarded-write
    /// sequence-attribution boundary (CAS gate -&gt; post-Set capture -&gt; verification reopen -&gt;
    /// read-back comparison -&gt; terminal outcome), for exactly one real
    /// <c>WriteTextIfSequenceMatchesAsync</c> dispatch. Raised synchronously on this monitor's own
    /// owner thread, exactly like <see cref="Changed"/>/<see cref="DiagnosticObserved"/> -- identical
    /// CALLBACK_THREAD_CONTRACT and identical subscriber-exception isolation (see
    /// <see cref="RaiseWriteDiagnostic"/>), so a broken diagnostic observer can never affect the real
    /// guarded write itself. With zero subscribers (the default) this is a single null-conditional
    /// check per emission point: no behavior change, no new native call, no new allocation beyond the
    /// event args record struct itself (see <see cref="ClipboardWriteDiagnosticEvent"/>'s own doc).
    /// </summary>
    public event EventHandler<ClipboardWriteDiagnosticEvent>? WriteDiagnosticObserved;

    public ClipboardChangeMonitor() : this(new Win32ClipboardMonitorNative())
    {
    }

    internal ClipboardChangeMonitor(
        IClipboardMonitorNative native,
        TimeSpan? stopTimeoutOverride = null,
        IClipboardTextNative? textNative = null,
        IForegroundTargetSource? foregroundSource = null)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
        _textNative = textNative ?? new Win32ClipboardTextNative();
        _foregroundSource = foregroundSource ?? new Win32ForegroundTargetSource();
        _stopTimeout = stopTimeoutOverride ?? StopTimeoutContractDefault;
    }

    /// <summary>
    /// Phase 3A.5 STEP4 -- the guarded, public read entry point. Identical mechanics to the
    /// internal primitive (see <see cref="ReadTextSnapshotAsync()"/>'s own doc), plus a
    /// FOREGROUND_EXECUTION_GUARD: <paramref name="expectedTarget"/> must still be the current
    /// foreground target, checked fresh on the owner thread immediately before
    /// <c>OpenClipboard</c> (CHECK 1) AND again immediately before any raw clipboard content is
    /// actually copied into managed memory (CHECK 2) -- never merely because an App-thread check
    /// once passed earlier. See <see cref="CheckForegroundTarget"/> for the exact mechanical
    /// comparison (PID + process name, ordinal-insensitive case, never window title/UIA/
    /// signature/AUMID/path).
    ///
    /// <paramref name="expectedTarget"/> is validated synchronously, on the CALLING thread,
    /// before anything is even enqueued: an unresolved snapshot, a zero PID, or a null/empty/
    /// whitespace process name is rejected as <see cref="ClipboardReadOutcome.InvalidExpectedTarget"/>
    /// with zero native calls of any kind.
    /// </summary>
    public Task<ClipboardTextReadResult> ReadTextSnapshotAsync(ForegroundTargetSnapshot expectedTarget)
    {
        if (!IsValidExpectedTarget(expectedTarget))
            return Task.FromResult(ClipboardTextReadResult.Failure(ClipboardReadOutcome.InvalidExpectedTarget));

        return EnqueueRead(expectedTarget);
    }

    /// <summary>
    /// Phase 3A.4 STEP3 -- reads a self-consistent (sequence, text) snapshot of the current
    /// clipboard's CF_UNICODETEXT content, entirely on the dedicated owner thread this monitor
    /// already owns (no second thread, no second message-only window -- Phase 3A.4 STEP3
    /// instruction's CORE_ARCHITECTURE_INVARIANT). The request is marshaled to the owner thread
    /// via a custom posted message and a <see cref="TaskCompletionSource{TResult}"/> -- never a
    /// blocking cross-thread SendMessage, so the caller (including a UI thread) is never
    /// blocked waiting for the owner thread's message loop.
    ///
    /// This method NEVER throws for an ordinary clipboard condition (format missing, clipboard
    /// busy, malformed data) -- those are reported via <see cref="ClipboardTextReadResult.Outcome"/>.
    /// It also never mutates the clipboard: no EmptyClipboard/SetClipboardData call exists
    /// anywhere in this type.
    ///
    /// PUBLIC_UNGUARDED_CLIPBOARD_API (Phase 3A.5 STEP4, resolved): this target-agnostic
    /// primitive is deliberately <c>internal</c>, not public -- the FOREGROUND_EXECUTION_GUARD is
    /// only meaningful if there is no way to bypass it from outside this assembly. It remains
    /// directly exercised by this assembly's own STEP3/STEP3.1 mechanics tests (via
    /// <c>InternalsVisibleTo</c>), which intentionally test clipboard read mechanics independent
    /// of target-guard concerns.
    /// </summary>
    internal Task<ClipboardTextReadResult> ReadTextSnapshotAsync() => EnqueueRead(expectedTarget: null);

    private Task<ClipboardTextReadResult> EnqueueRead(ForegroundTargetSnapshot? expectedTarget)
    {
        var tcs = new TaskCompletionSource<ClipboardTextReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // LIFECYCLE_RACES (Phase 3A.4 STEP3 report): this check-and-enqueue happens under the
        // same _gate lock the owner thread's shutdown path uses to flip _running=false and
        // drain _pendingReads (see OwnerThreadMain's finally block). Whichever side acquires
        // the lock first "wins" a race with a concurrent Stop/Dispose -- either this request is
        // enqueued before the drain (and the drain then completes it as NotRunning), or it
        // observes _running already false and never enqueues at all. Either way the returned
        // Task always completes; it is never left permanently pending.
        lock (_gate)
        {
            if (!_startCalled || !_running || _disposed)
            {
                tcs.SetResult(ClipboardTextReadResult.Failure(ClipboardReadOutcome.NotRunning));
                return tcs.Task;
            }

            _pendingReads.Enqueue(new PendingRead(tcs, expectedTarget));
        }

        // POSTMESSAGE_FAILURE_HANDLING (Phase 3A.4 STEP3.1): PostMessageW can itself fail. If it
        // does, the wakeup the owner thread needed to ever process this request will never
        // arrive -- so this request is completed directly, right here, rather than left waiting
        // for a signal that is never coming. The stale entry left behind in _pendingReads (this
        // exact TaskCompletionSource, already completed) is harmless: ProcessOnePendingRead
        // skips any already-completed entry it dequeues rather than executing a real clipboard
        // read for it, and DrainPendingReadsAsNotRunning's TrySetResult on an already-completed
        // instance is a safe no-op. No queue removal/rollback machinery is needed.
        if (!_native.PostReadWorkSignal(_hwnd, out int postError))
        {
            tcs.TrySetResult(ClipboardTextReadResult.Failure(ClipboardReadOutcome.NativeFailure, postError));
        }

        return tcs.Task;
    }

    /// <summary>
    /// Phase 3A.5 STEP4 -- the guarded, public write entry point. Identical mechanics to the
    /// internal primitive (see <see cref="WriteTextIfSequenceMatchesAsync(uint, string)"/>'s own
    /// doc), plus a FOREGROUND_EXECUTION_GUARD: <paramref name="expectedTarget"/> must still be
    /// the current foreground target, checked fresh on the owner thread immediately BEFORE the
    /// replacement HGLOBAL is even prepared (CHECK 1 -- if the authorized target is already gone
    /// there is no reason to allocate/copy replacement text into unmanaged memory at all) AND
    /// again immediately before <c>EmptyClipboard</c> (CHECK 2, after the sequence-CAS gate has
    /// already been confirmed). Target equality and sequence equality are independent AND
    /// conditions -- neither substitutes for the other. See <see cref="CheckForegroundTarget"/>.
    ///
    /// <paramref name="expectedTarget"/> is validated synchronously, on the CALLING thread, before
    /// anything is even enqueued, exactly like <paramref name="expectedSequence"/> already is:
    /// an unresolved snapshot, a zero PID, or a null/empty/whitespace process name is rejected as
    /// <see cref="ClipboardWriteOutcome.InvalidExpectedTarget"/> with zero native calls.
    /// </summary>
    public Task<ClipboardWriteResult> WriteTextIfSequenceMatchesAsync(
        ForegroundTargetSnapshot expectedTarget, uint expectedSequence, string replacementText)
    {
        ArgumentNullException.ThrowIfNull(replacementText);

        if (!IsValidExpectedTarget(expectedTarget))
            return Task.FromResult(ClipboardWriteResult.Failure(ClipboardWriteOutcome.InvalidExpectedTarget, mutated: false));

        return ValidateAndEnqueueWrite(expectedTarget, expectedSequence, replacementText);
    }

    /// <summary>
    /// Phase 3A.4 STEP4 -- replaces the clipboard's CF_UNICODETEXT content with
    /// <paramref name="replacementText"/>, but ONLY if the clipboard's current
    /// GetClipboardSequenceNumber() still equals <paramref name="expectedSequence"/> at the
    /// moment the clipboard is actually opened (a compare-and-swap guard against overwriting
    /// content some other actor changed since the caller last observed it). Runs entirely on the
    /// dedicated owner thread, marshaled the same way as <see cref="ReadTextSnapshotAsync()"/> (a
    /// posted custom message + a <see cref="TaskCompletionSource{TResult}"/> -- never a blocking
    /// cross-thread SendMessage).
    ///
    /// Two validations happen synchronously, on the CALLING thread, before anything is even
    /// enqueued -- neither can ever cause a native mutation:
    ///   - <paramref name="expectedSequence"/> == 0 is rejected outright (0 is never a trustworthy
    ///     compare-and-swap baseline -- see <see cref="ClipboardChangeNotification.HasReliableSequence"/>'s
    ///     identical rule);
    ///   - <paramref name="replacementText"/> containing an embedded '\0', or a length that would
    ///     overflow the checked UTF-16-plus-terminator byte-size computation, is rejected as
    ///     <see cref="ClipboardWriteOutcome.InvalidText"/>.
    /// Empty string and whitespace-only text are both allowed. No normalization/trim/case-fold is
    /// ever applied to <paramref name="replacementText"/> -- it is written and read back exactly.
    ///
    /// This method never throws for an ordinary clipboard condition (busy, sequence changed,
    /// read-back mismatch, ...) -- those are reported via <see cref="ClipboardWriteResult.Outcome"/>.
    /// A <see cref="ClipboardWriteResult.Outcome"/> of <see cref="ClipboardWriteOutcome.Success"/>
    /// still is NOT <c>Privon.Core.ProtectionState.Verified</c> -- see this type's own class doc.
    ///
    /// PUBLIC_UNGUARDED_CLIPBOARD_API (Phase 3A.5 STEP4, resolved): deliberately <c>internal</c>,
    /// not public -- see <see cref="ReadTextSnapshotAsync()"/>'s identical note. Its own STEP4/
    /// STEP4.1 semantics (sequence-CAS, ownership transfer, read-back verification, self-write
    /// suppression) are entirely unchanged by the guard's addition.
    /// </summary>
    internal Task<ClipboardWriteResult> WriteTextIfSequenceMatchesAsync(uint expectedSequence, string replacementText)
    {
        ArgumentNullException.ThrowIfNull(replacementText);
        return ValidateAndEnqueueWrite(expectedTarget: null, expectedSequence, replacementText);
    }

    // Shared TEXT_VALIDATION + enqueue body for both the guarded and internal write entry
    // points -- expectedTarget's own InvalidExpectedTarget validation already happened (or was
    // structurally skipped, for the internal null-target primitive) in each public caller above.
    private Task<ClipboardWriteResult> ValidateAndEnqueueWrite(
        ForegroundTargetSnapshot? expectedTarget, uint expectedSequence, string replacementText)
    {
        if (expectedSequence == 0)
            return Task.FromResult(ClipboardWriteResult.Failure(ClipboardWriteOutcome.InvalidExpectedSequence, mutated: false));

        if (replacementText.Contains('\0'))
            return Task.FromResult(ClipboardWriteResult.Failure(ClipboardWriteOutcome.InvalidText, mutated: false));

        // TEXT_VALIDATION: checked byte-size computation, entirely on the calling thread, before
        // any allocation is even attempted -- (length + 1) UTF-16 code units (text plus the NUL
        // terminator CF_UNICODETEXT requires), each 2 bytes. `checked` throws OverflowException on
        // int overflow for a string near int.MaxValue in length; that is still "no native mutation
        // occurred," just reported as InvalidText rather than propagated as an unhandled overflow.
        try
        {
            checked
            {
                _ = (replacementText.Length + 1) * 2;
            }
        }
        catch (OverflowException)
        {
            return Task.FromResult(ClipboardWriteResult.Failure(ClipboardWriteOutcome.InvalidText, mutated: false));
        }

        var tcs = new TaskCompletionSource<ClipboardWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Same LIFECYCLE_RACES discipline as ReadTextSnapshotAsync -- check-and-enqueue under
        // _gate, racing safely against the owner thread's shutdown drain.
        lock (_gate)
        {
            if (!_startCalled || !_running || _disposed)
            {
                tcs.SetResult(ClipboardWriteResult.Failure(ClipboardWriteOutcome.NotRunning, mutated: false));
                return tcs.Task;
            }

            _pendingWrites.Enqueue(new PendingWrite(tcs, expectedTarget, expectedSequence, replacementText));
        }

        // Same POSTMESSAGE_FAILURE_HANDLING discipline as ReadTextSnapshotAsync: if the wakeup
        // itself cannot be posted, complete the request directly rather than leave it stranded.
        // No native mutation has happened at this point -- ClipboardMutated=false.
        if (!_native.PostWriteWorkSignal(_hwnd, out int postError))
        {
            tcs.TrySetResult(ClipboardWriteResult.Failure(ClipboardWriteOutcome.NativeFailure, mutated: false, postError));
        }

        return tcs.Task;
    }

    // INVALID_TARGET_POLICY (Phase 3A.5 STEP4): mirrors the existing expectedSequence==0 /
    // embedded-NUL caller-thread-reject pattern exactly -- structurally invalid tokens are
    // rejected before any queueing, PostMessage, HGLOBAL allocation, or native call of any kind.
    // A null/empty/whitespace ProcessName is rejected defensively even though
    // ForegroundTargetSnapshot.IsResolved==true "should" imply a real name -- 0 is never trusted
    // as a valid PID elsewhere in this codebase for the identical reason (see
    // ForegroundTargetInspector.Capture()'s own defensive processId==0 check).
    private static bool IsValidExpectedTarget(ForegroundTargetSnapshot target) =>
        target.IsResolved && target.ProcessId != 0 && !string.IsNullOrWhiteSpace(target.ProcessName);

    /// <summary>
    /// Starts the owner thread and blocks until either every startup step (window class
    /// registration, message-only window creation, AddClipboardFormatListener) has actually
    /// succeeded, or one of them has failed -- never a "half-started" return (Phase 3A.4 STEP1
    /// report's Start-semantics requirement). On failure, whatever partial state was created is
    /// already cleaned up (in reverse order) before this method throws.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startCalled)
                throw new InvalidOperationException(
                    "Start has already been called on this ClipboardChangeMonitor instance -- it is single-use.");
            _startCalled = true;
        }

        _ownerThread = new Thread(OwnerThreadMain)
        {
            IsBackground = true,
            Name = "PrivonClipboardMonitor",
        };
        _ownerThread.Start();

        _startupSignal.Wait();
        _startupFailure?.Throw();
    }

    /// <summary>
    /// Idempotent and safe before Start, after a failed Start, or after a prior Stop. Posts a
    /// shutdown request to the owner thread's own message queue (thread-safe per PostMessage's
    /// documented contract) rather than destroying the HWND cross-thread.
    /// </summary>
    public void Stop()
    {
        Thread? ownerThread;
        lock (_gate)
        {
            if (!_startCalled || !_running) return;
            ownerThread = _ownerThread;
        }

        _native.PostShutdown(_hwnd);
        ownerThread?.Join(_stopTimeout);

        // A timed-out Join means the owner thread did not confirm exit within the bounded
        // window (CLIPBOARD_MONITOR_STOP_TIMEOUT) -- this must never be silently treated as a
        // clean stop (fail-closed). No raw clipboard content is or could be in this message --
        // only the operation, the stage, and the timeout value.
        if (ownerThread is { IsAlive: true })
        {
            throw new InvalidOperationException(
                $"Clipboard monitor owner thread did not exit within the shutdown timeout ({_stopTimeout.TotalSeconds:F0}s).");
        }
    }

    /// <summary>
    /// DISPOSE_FAILURE_BEHAVIOR (Phase 3A.4 STEP2.1): idempotent on success, but NOT
    /// unconditionally silent -- a cleanup failure (e.g. the same shutdown-timeout <see cref="Stop"/>
    /// can throw) is never swallowed here. There is no IDisposable contract requiring Dispose to
    /// never throw, and hiding a real native-resource-cleanup failure behind a falsely "fully
    /// disposed" state would let a caller believe the listener/thread were gone when they are
    /// not. Concretely: this method is marked fully disposed -- making a later call a safe
    /// no-op -- ONLY once the underlying <see cref="Stop"/> has actually completed without
    /// throwing. If it throws, this instance is left exactly as retriable as before the call, so
    /// a later <see cref="Dispose"/> call genuinely retries cleanup rather than silently
    /// no-op'ing over an unresolved failure.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }

        Stop();

        lock (_gate)
        {
            _disposed = true;
        }

        _startupSignal.Dispose();
    }

    private void OwnerThreadMain()
    {
        nint hwnd = 0;
        bool classRegistered = false;
        bool windowCreated = false;
        bool listenerAdded = false;
        bool startupFailed = false;

        try
        {
            if (!_native.RegisterWindowClass(_windowClassName, out int classError))
                throw new InvalidOperationException($"RegisterClassExW failed (Win32 error {classError}).");
            classRegistered = true;

            if (!_native.CreateMessageOnlyWindow(_windowClassName, out hwnd, out int windowError))
                throw new InvalidOperationException($"CreateWindowExW failed (Win32 error {windowError}).");
            windowCreated = true;
            _hwnd = hwnd;

            if (!_native.AddClipboardFormatListener(hwnd, out int listenerError))
                throw new InvalidOperationException($"AddClipboardFormatListener failed (Win32 error {listenerError}).");
            listenerAdded = true;

            _running = true;
            _startupSignal.Set();

            RunMessageLoop(hwnd);
        }
        catch (Exception ex)
        {
            _startupFailure = ExceptionDispatchInfo.Capture(ex);
            startupFailed = true;
        }
        finally
        {
            // _running=false and draining _pendingReads/_pendingWrites happen together, under
            // _gate, as ONE atomic step -- this is what makes ReadTextSnapshotAsync's and
            // WriteTextIfSequenceMatchesAsync's own lock-protected check-and-enqueue race-free
            // against shutdown (see those methods' doc comments).
            lock (_gate)
            {
                _running = false;
                DrainPendingReadsAsNotRunning();
                DrainPendingWritesAsNotRunning();
            }

            // BUG-006: one opportunistic, best-effort attempt to resolve a process-wide quarantined
            // rollback handle as part of this monitor's own shutdown -- never a retry loop, never a
            // new thread/timer. Safe here because this instance's own message loop has already
            // fully drained (no in-flight write of THIS instance can still be running), matching the
            // write-admission path's own exclusivity discipline. See its own doc for the full
            // cross-instance/shutdown-vs-write contract. Deliberately swallowed: this is a
            // best-effort opportunistic step for a handle that may not even belong to this
            // instance (the slot is process-wide) -- it must never be allowed to throw and skip the
            // mandatory listener/window/class teardown immediately below, which every caller of
            // Stop()/Dispose() already depends on actually running.
            try { TryResolveQuarantineAtShutdown(); } catch { /* best-effort only -- see above */ }

            // Reverse-order cleanup, and only for steps that actually succeeded -- listener
            // removed before the window is destroyed, window destroyed before the class is
            // unregistered, matching the required teardown order exactly.
            if (listenerAdded) _native.RemoveClipboardFormatListener(hwnd, out _);
            if (windowCreated) _native.DestroyWindow(hwnd, out _);
            if (classRegistered) _native.UnregisterWindowClass(_windowClassName, out _);

            // Startup-FAILURE signaling is deliberately deferred to here, after cleanup, not
            // raised from the catch block above -- this is what makes the documented Start()
            // contract ("on failure, whatever partial state was created is already cleaned up
            // ... before this method throws") an actual guarantee rather than a race. The
            // success path signals earlier, right after the listener is registered (see above)
            // -- it must never signal a second time here.
            if (startupFailed) _startupSignal.Set();
        }
    }

    // Must only be called while holding _gate -- see the call sites' own comments.
    private void DrainPendingReadsAsNotRunning()
    {
        while (_pendingReads.TryDequeue(out var pending))
        {
            pending.Tcs.TrySetResult(ClipboardTextReadResult.Failure(ClipboardReadOutcome.NotRunning));
        }
    }

    // Must only be called while holding _gate -- see the call sites' own comments.
    // Never a native mutation: shutdown means no write for this queued request ever happens.
    private void DrainPendingWritesAsNotRunning()
    {
        while (_pendingWrites.TryDequeue(out var pending))
        {
            pending.Tcs.TrySetResult(ClipboardWriteResult.Failure(ClipboardWriteOutcome.NotRunning, mutated: false));
        }
    }

    private void RunMessageLoop(nint hwnd)
    {
        while (true)
        {
            var kind = _native.WaitForNextMessage(hwnd);
            switch (kind)
            {
                case ClipboardMonitorMessageKind.Shutdown:
                    return;

                case ClipboardMonitorMessageKind.ReadWork:
                    ProcessOnePendingRead(hwnd);
                    break;

                case ClipboardMonitorMessageKind.WriteWork:
                    ProcessOnePendingWrite(hwnd);
                    break;

                case ClipboardMonitorMessageKind.ClipboardUpdate:
                    RaiseChanged();
                    break;
            }
        }
    }

    private void RaiseChanged()
    {
        // Phase 3C STEP41.1 -- diagnostic-only local correlation id, incremented exactly once per
        // real RaiseChanged invocation (i.e. once per actual WM_CLIPBOARDUPDATE the message loop
        // dispatched). Shared by every diagnostic event this specific invocation raises below, so a
        // reader can pair NativeNotificationReceived with its own terminal
        // SelfWriteSuppressed/ExternalChangeRaised event even when SequenceNumber is 0.
        long localEventId = ++_diagnosticEventCounter;

        uint sequence = _native.GetClipboardSequenceNumber();
        bool reliable = sequence != 0;

        RaiseDiagnostic(new ClipboardMonitorDiagnosticEvent(
            localEventId, ClipboardMonitorDiagnosticKind.NativeNotificationReceived, sequence, reliable));

        // SELF_WRITE_SUPPRESSION_MARKER_LIFECYCLE (Phase 3A.4 STEP4.1, corrected): only ever
        // compared against a sequence number -- never against raw/protected text. A 0 sequence is
        // never trusted for this comparison either way, matching the "0 is never usable for a
        // guarded compare" rule applied everywhere else in this type -- it neither suppresses nor
        // retires the marker, since 0 cannot prove a different clipboard state exists.
        if (reliable)
        {
            if (_lastSuccessfulWriteSequence.HasValue && sequence == _lastSuccessfulWriteSequence.Value)
            {
                // This is our own successful write becoming visible via WM_CLIPBOARDUPDATE, not
                // an external change -- swallow it. The marker is NOT consumed by this match: it
                // stays armed and suppresses every later notification carrying this same
                // reliable sequence too, because multiple WM_CLIPBOARDUPDATE messages may already
                // be queued for our own EmptyClipboard/SetClipboardData pair and all observe the
                // same final sequence once processed. Clearing after the first match would leak a
                // second self-write event out to a future App-level subscriber.
                //
                // Phase 3C STEP41.1: this is the exact, previously-invisible-to-App boundary the
                // manual QA diagnostic gap called out -- a self-write-suppressed native
                // notification now produces this one terminal diagnostic event and nothing else
                // (Changed is never invoked, so no App attempt is ever expected to follow).
                RaiseDiagnostic(new ClipboardMonitorDiagnosticEvent(
                    localEventId, ClipboardMonitorDiagnosticKind.SelfWriteSuppressed, sequence, reliable));
                return;
            }

            if (_lastSuccessfulWriteSequence.HasValue)
            {
                // A DIFFERENT reliable sequence is proof of a newer external/new clipboard
                // change -- only now does the marker retire (sequence numbers strictly increase
                // and are never reused, so a retired marker could never spuriously match a
                // future write's own sequence).
                _lastSuccessfulWriteSequence = null;
            }
        }

        bool hasUnicodeText = _native.IsUnicodeTextAvailable();
        var notification = new ClipboardChangeNotification(sequence, reliable, hasUnicodeText);

        // Phase 3C STEP41.1: fired immediately before Changed itself, carrying the exact same
        // sequence/reliability that notification also carries -- see
        // ClipboardMonitorDiagnosticEvent's own CROSS_LAYER_CORRELATION doc for how this lets an
        // App-layer trace entry be matched to this exact native event by SequenceNumber equality.
        RaiseDiagnostic(new ClipboardMonitorDiagnosticEvent(
            localEventId, ClipboardMonitorDiagnosticKind.ExternalChangeRaised, sequence, reliable));

        try
        {
            Changed?.Invoke(this, notification);
        }
        catch
        {
            // Intentionally swallowed -- see the Changed event's doc comment.
        }
    }

    // Phase 3C STEP41.1: mirrors Changed's own subscriber-exception-isolation precedent exactly --
    // a diagnostic observer's own failure must never corrupt the owner thread's message loop, skip
    // native resource cleanup, or in any way affect real clipboard monitoring. This is PASSIVE
    // OBSERVATION ONLY: no retry, no queuing, no file I/O, no allocation beyond the record struct
    // itself -- any persistence belongs entirely to whatever upper-layer subscriber this event
    // reaches (see DiagnosticObserved's own doc).
    private void RaiseDiagnostic(ClipboardMonitorDiagnosticEvent diagnosticEvent)
    {
        try
        {
            DiagnosticObserved?.Invoke(this, diagnosticEvent);
        }
        catch
        {
            // Intentionally swallowed -- see DiagnosticObserved's own doc comment.
        }
    }

    // Phase 3C STEP41.2: identical subscriber-exception-isolation precedent as RaiseDiagnostic
    // above -- a write-diagnostic observer's own failure must never corrupt the owner thread, skip
    // native resource cleanup, or affect the real guarded write in any way. PASSIVE OBSERVATION
    // ONLY, same as RaiseDiagnostic.
    private void RaiseWriteDiagnostic(ClipboardWriteDiagnosticEvent diagnosticEvent)
    {
        try
        {
            WriteDiagnosticObserved?.Invoke(this, diagnosticEvent);
        }
        catch
        {
            // Intentionally swallowed -- see WriteDiagnosticObserved's own doc comment.
        }
    }

    // Dequeues and completes exactly one pending read request -- ReadTextSnapshotAsync posts
    // one ReadWork wakeup per enqueued request, so this 1:1 pairing is correct in the normal
    // case. An entry can already be completed here if its own PostReadWorkSignal call failed
    // (see ReadTextSnapshotAsync) -- such an entry must never trigger a real clipboard read, so
    // this loops past any already-completed entries until it finds a live one (or the queue is
    // empty). This is the only production behavior change from a strict 1:1 pairing.
    private void ProcessOnePendingRead(nint hwnd)
    {
        while (_pendingReads.TryDequeue(out var pending))
        {
            if (pending.Tcs.Task.IsCompleted) continue;
            pending.Tcs.TrySetResult(ExecuteRead(hwnd, pending.ExpectedTarget));
            return;
        }
    }

    // Same skip-stale-completed-entries discipline as ProcessOnePendingRead -- a request whose
    // own PostWriteWorkSignal call failed is already completed and must never trigger a real
    // clipboard mutation when a later, unrelated WriteWork wakeup happens to dequeue it.
    private void ProcessOnePendingWrite(nint hwnd)
    {
        while (_pendingWrites.TryDequeue(out var pending))
        {
            if (pending.Tcs.Task.IsCompleted) continue;
            pending.Tcs.TrySetResult(ExecuteWrite(hwnd, pending.ExpectedTarget, pending.ExpectedSequence, pending.ReplacementText));
            return;
        }
    }

    // READ_FLOW (Phase 3A.4 STEP3/STEP3.1 report, extended by Phase 3A.5 STEP4's
    // FOREGROUND_READ_EXECUTION_GUARD): [CHECK 1 if expectedTarget != null] -> OpenClipboard ->
    // [sequence + format check -> CHECK 2 if expectedTarget != null -> GetClipboardData ->
    // GlobalSize -> GlobalLock -> bounded managed copy -> GlobalUnlock] -> CloseClipboard. Every
    // exit path closes the clipboard exactly once and never calls GlobalFree on a clipboard-owned
    // handle. No EmptyClipboard/SetClipboardData call exists anywhere in this method or this type.
    // expectedTarget is null only for the internal, target-agnostic primitive -- in that case
    // both CHECK 1 and CHECK 2 are structurally skipped, exactly reproducing pre-STEP4 behavior.
    private ClipboardTextReadResult ExecuteRead(nint hwnd, ForegroundTargetSnapshot? expectedTarget)
    {
        // CHECK 1 (Phase 3A.5 STEP4): fresh foreground check BEFORE OpenClipboard is ever called
        // -- an already-known-wrong target never even gets a clipboard session opened for it.
        if (expectedTarget is { } expected1)
        {
            var guardResult = CheckForegroundTarget(expected1);
            if (guardResult != ForegroundGuardResult.Matched)
                return ClipboardTextReadResult.Failure(MapReadGuardOutcome(guardResult));
        }

        if (!_textNative.OpenClipboard(hwnd, out int openError))
            return ClipboardTextReadResult.Failure(ClipboardReadOutcome.Busy, openError);

        var result = ReadWhileClipboardOpen(hwnd, expectedTarget);

        // FAILURE_PRECEDENCE (Phase 3A.4 STEP3.1): a CloseClipboard failure overrides whatever
        // ReadWhileClipboardOpen determined -- Success included. A caller must never be told the
        // read succeeded (or told any other specific outcome) when the clipboard session itself
        // did not close cleanly; this is a single resource/lifecycle-cleanup failure, reported as
        // exactly one outcome with exactly one Win32 error code, never accumulated alongside
        // whatever the read body's own outcome was. Unchanged by the guard's addition -- CHECK 2
        // failing is just one more branch this precedence rule already applies to.
        if (!_textNative.CloseClipboard(out int closeError))
            return ClipboardTextReadResult.Failure(ClipboardReadOutcome.NativeFailure, closeError);

        return result;
    }

    // The body of the read, entirely inside the OpenClipboard/CloseClipboard bracket that
    // ExecuteRead already established. Sequence and format-availability are captured together
    // here, inside that same bracket, so the snapshot is self-consistent -- no other process can
    // successfully change the clipboard while it is open (Phase 3A.4 STEP1 report's
    // SNAPSHOT_SEMANTICS finding).
    private ClipboardTextReadResult ReadWhileClipboardOpen(nint hwnd, ForegroundTargetSnapshot? expectedTarget)
    {
        uint sequence = _native.GetClipboardSequenceNumber();

        if (!_native.IsUnicodeTextAvailable())
            return ClipboardTextReadResult.Failure(ClipboardReadOutcome.FormatUnavailable);

        // CHECK 2 (Phase 3A.5 STEP4): a second fresh foreground check, positioned AFTER the
        // sequence/format metadata capture (neither exposes content -- a number and a boolean)
        // but IMMEDIATELY BEFORE TryReadUnicodeTextBody, which is the actual raw-content-copying
        // step. If this fails, TryReadUnicodeTextBody/GetClipboardData are never called -- no raw
        // clipboard content ever enters managed memory for a target that changed between CHECK 1
        // and this point.
        if (expectedTarget is { } expected2)
        {
            var guardResult = CheckForegroundTarget(expected2);
            if (guardResult != ForegroundGuardResult.Matched)
                return ClipboardTextReadResult.Failure(MapReadGuardOutcome(guardResult));
        }

        if (!TryReadUnicodeTextBody(out string? text, out ClipboardReadOutcome failureOutcome, out int? win32Error))
            return ClipboardTextReadResult.Failure(failureOutcome, win32Error);

        return ClipboardTextReadResult.Success(new ClipboardTextSnapshot(sequence, sequence != 0, text!));
    }

    // Shared core of "read CF_UNICODETEXT while the clipboard is already open" -- GetClipboardData
    // -> GlobalSize -> GlobalLock -> bounded managed copy -> parse -> GlobalUnlock, implemented
    // exactly once and reused by both a plain read (ReadWhileClipboardOpen) and post-write
    // verification (VerifyWhileClipboardOpen) -- Phase 3A.4 STEP4 instruction's "existing read
    // parser/native calls 재사용" requirement. Callers are responsible for their own
    // format-availability check (IsUnicodeTextAvailable) before calling this -- it is not
    // repeated here, matching how ReadWhileClipboardOpen already behaved before this refactor.
    private bool TryReadUnicodeTextBody(out string? text, out ClipboardReadOutcome failureOutcome, out int? win32Error)
    {
        text = null;
        failureOutcome = default;
        win32Error = null;

        if (!_textNative.TryGetUnicodeTextHandle(out nint dataHandle, out int dataError))
        {
            failureOutcome = ClipboardReadOutcome.NativeFailure;
            win32Error = dataError;
            return false;
        }

        if (!_textNative.TryGetGlobalSize(dataHandle, out nuint byteSize, out int sizeError))
        {
            failureOutcome = ClipboardReadOutcome.NativeFailure;
            win32Error = sizeError;
            return false;
        }

        // Untrusted upper bound (Phase 3A.4 STEP1 instruction's CLIPBOARD_DATA_IS_UNTRUSTED_INPUT
        // rule) -- never allow it to overflow a managed array length before we even attempt to
        // copy anything.
        if (byteSize > int.MaxValue)
        {
            failureOutcome = ClipboardReadOutcome.MalformedData;
            return false;
        }

        if (!_textNative.TryGlobalLock(dataHandle, out nint pointer, out int lockError))
        {
            failureOutcome = ClipboardReadOutcome.NativeFailure;
            win32Error = lockError;
            return false;
        }

        string? parsed;
        bool unlockOk;
        int unlockError = 0;
        try
        {
            // Marshal.Copy bounds the read to exactly byteSize -- never scans past it looking
            // for a NUL the way Marshal.PtrToStringUni would.
            var length = (int)byteSize;
            var buffer = new byte[length];
            Marshal.Copy(pointer, buffer, 0, length);
            parsed = ClipboardTextParser.Parse(buffer);
        }
        finally
        {
            // GLOBALUNLOCK_SEMANTICS (Phase 3A.4 STEP3.1): GlobalUnlock's own boolean return is
            // ambiguous by Microsoft's own documented contract -- FALSE can mean either "lock
            // count reached zero" (normal, successful) or a genuine failure; the two are only
            // distinguishable via GetLastError() captured immediately afterward. TryGlobalUnlock
            // performs that distinction (see its own doc); always attempted here, exception-safe.
            unlockOk = _textNative.TryGlobalUnlock(dataHandle, out unlockError);
        }

        if (!unlockOk)
        {
            failureOutcome = ClipboardReadOutcome.NativeFailure;
            win32Error = unlockError;
            return false;
        }

        if (parsed is null)
        {
            failureOutcome = ClipboardReadOutcome.MalformedData;
            return false;
        }

        text = parsed;
        return true;
    }

    // WRITE_FLOW (Phase 3A.4 STEP4, extended by Phase 3A.5 STEP4's
    // FOREGROUND_WRITE_EXECUTION_GUARD): [CHECK 1 if expectedTarget != null] -> prepare the
    // replacement HGLOBAL (to keep the clipboard-open window as short as possible --
    // HGLOBAL_PREALLOCATION_VS_MS_SAMPLE_ORDERING) -> open/[sequence gate]/[CHECK 2]/mutate/close
    // -> then -- only for a genuinely successful mutation with a reliable write sequence -- reopen
    // once more to verify the read-back matches exactly. CHECK 1 sits BEFORE HGLOBAL preparation,
    // not merely before OpenClipboard: if the authorized target is already gone there is no
    // reason to allocate/copy replacement text into unmanaged memory at all.
    private ClipboardWriteResult ExecuteWrite(
        nint hwnd, ForegroundTargetSnapshot? expectedTarget, uint expectedSequence, string replacementText)
    {
        // Phase 3C STEP41.2: a thin wrapper around the unchanged write logic (now ExecuteWriteCore)
        // -- adds exactly two diagnostic emissions (WriteStarted before, WriteCompleted after) and
        // nothing else. writeId is minted here, once per real ExecuteWrite invocation, and threaded
        // through every helper below that raises its own diagnostic event for this same write.
        long writeId = ++_writeDiagnosticCounter;
        RaiseWriteDiagnostic(new ClipboardWriteDiagnosticEvent(
            writeId, ClipboardWriteDiagnosticKind.WriteStarted, expectedSequence, null, null, null, null, null));

        var result = ExecuteWriteCore(writeId, hwnd, expectedTarget, expectedSequence, replacementText);

        RaiseWriteDiagnostic(new ClipboardWriteDiagnosticEvent(
            writeId, ClipboardWriteDiagnosticKind.WriteCompleted, null, null, null, null, result.Outcome, result.ClipboardMutated));
        return result;
    }

    // The exact, unmodified write body from before Phase 3C STEP41.2 -- moved into its own method
    // only so ExecuteWrite's wrapper above can observe (and diagnostically report on) whatever
    // ClipboardWriteResult this returns, from every branch, without duplicating that observation at
    // each individual return statement. No control flow, branching, or native-call ordering below
    // differs from before this STEP.
    private ClipboardWriteResult ExecuteWriteCore(
        long writeId, nint hwnd, ForegroundTargetSnapshot? expectedTarget, uint expectedSequence, string replacementText)
    {
        // CHECK 1 (Phase 3A.5 STEP4).
        if (expectedTarget is { } expected1)
        {
            var guardResult = CheckForegroundTarget(expected1);
            if (guardResult != ForegroundGuardResult.Matched)
                return ClipboardWriteResult.Failure(MapWriteGuardOutcome(guardResult), mutated: false);
        }

        if (!TryPrepareReplacementHGlobal(replacementText, out nint hGlobal, out ClipboardWriteResult prepFailure))
            return prepFailure;

        if (!_textNative.OpenClipboard(hwnd, out int openError))
        {
            // Pre-mutation failure -- hGlobal is still ours, and always will be (Set was never
            // even attempted).
            var (freeOutcome, freeErr) = FreeOwnedHGlobal(hGlobal, ClipboardWriteOutcome.Busy, openError);
            return ClipboardWriteResult.Failure(freeOutcome, mutated: false, freeErr);
        }

        var (outcome, mutated, writeSequence, mutationError) = MutateWhileClipboardOpen(writeId, expectedSequence, expectedTarget, hGlobal);

        // FAILURE_PRECEDENCE (mirrors ExecuteRead/Phase 3A.4 STEP3.1): a CloseClipboard failure
        // overrides whatever the mutation body determined -- Success included -- but the already-
        // determined `mutated` flag (and any hGlobal ownership decision already made inside
        // MutateWhileClipboardOpen) is preserved exactly as-is.
        if (!_textNative.CloseClipboard(out int closeError))
            return ClipboardWriteResult.Failure(ClipboardWriteOutcome.NativeFailure, mutated, closeError);

        if (outcome != ClipboardWriteOutcome.Success)
            return ClipboardWriteResult.Failure(outcome, mutated, mutationError);

        // WRITE_SEQUENCE_CAPTURE: writeSequence was captured while the clipboard was still open,
        // immediately after SetClipboardData succeeded -- never after this CloseClipboard call --
        // so it can never be confused with a later, different actor's change (see
        // MutateWhileClipboardOpen's own doc). A 0 here means the mutation genuinely succeeded but
        // has no reliable marker to verify against.
        if (writeSequence == 0)
            return ClipboardWriteResult.Failure(ClipboardWriteOutcome.VerificationUnavailable, mutated: true);

        return VerifyWrite(writeId, hwnd, writeSequence, replacementText);
    }

    // HGLOBAL_OWNERSHIP: allocate/lock/fill/unlock a fresh GMEM_MOVEABLE block containing
    // replacementText as CF_UNICODETEXT (UTF-16LE + NUL terminator), entirely before any clipboard
    // API is touched. On any failure here, hGlobal is freed before returning (PRIVON still owns
    // it, and nothing else ever will) and out hGlobal is left as 0.
    private bool TryPrepareReplacementHGlobal(string replacementText, out nint hGlobal, out ClipboardWriteResult failure)
    {
        // No embedded NUL, no overflow -- WriteTextIfSequenceMatchesAsync already validated both
        // before this ever reached the owner thread.
        byte[] bytes = Encoding.Unicode.GetBytes(replacementText + '\0');

        if (!_textNative.TryGlobalAlloc((nuint)bytes.Length, out hGlobal, out int allocError))
        {
            failure = ClipboardWriteResult.Failure(ClipboardWriteOutcome.NativeFailure, mutated: false, allocError);
            return false;
        }

        if (!_textNative.TryGlobalLock(hGlobal, out nint pointer, out int lockError))
        {
            var (freeOutcome, freeErr) = FreeOwnedHGlobal(hGlobal, ClipboardWriteOutcome.NativeFailure, lockError);
            hGlobal = 0;
            failure = ClipboardWriteResult.Failure(freeOutcome, mutated: false, freeErr);
            return false;
        }

        bool unlockOk;
        int unlockError = 0;
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
        }
        finally
        {
            unlockOk = _textNative.TryGlobalUnlock(hGlobal, out unlockError);
        }

        if (!unlockOk)
        {
            var (freeOutcome, freeErr) = FreeOwnedHGlobal(hGlobal, ClipboardWriteOutcome.NativeFailure, unlockError);
            hGlobal = 0;
            failure = ClipboardWriteResult.Failure(freeOutcome, mutated: false, freeErr);
            return false;
        }

        failure = default;
        return true;
    }

    // BUG-006 (exception-atomic correction, supersedes the earlier fill-late design): reads the
    // CURRENT CF_UNICODETEXT content -- reusing the exact same TryReadUnicodeTextBody helper
    // ReadWhileClipboardOpen/VerifyWhileClipboardOpen already share -- allocates a correctly-sized
    // rollback buffer, and FULLY copies the RAW content into it, ALL entirely BEFORE EmptyClipboard
    // is ever called. PRE_DESTRUCTIVE_ROLLBACK (frozen intent): no exception-capable RAW native
    // copy may remain after crossing the destructive boundary -- correctness/data-loss resistance
    // outranks the earlier microseconds-scale reduction in RAW native-memory lifetime the fill-late
    // design traded for. Deliberately does NOT resolve/free `hGlobal` itself on failure (whether a
    // false return or an exception, e.g. from Marshal.Copy) -- ownership of an allocated-but-not-
    // fully-prepared handle is the CALLER's exception-atomic responsibility (see
    // MutateWhileClipboardOpen's own rollbackPending/finally doc) precisely so there is exactly ONE
    // place in this type that ever resolves a rollback handle, never two independently-written
    // copies of that logic. `hGlobal` is only ever 0 if allocation itself never succeeded.
    private bool TryPrepareRollbackFull(out nint hGlobal, out int byteLength, out ClipboardWriteResult failure)
    {
        hGlobal = 0;
        byteLength = 0;

        if (!TryReadUnicodeTextBody(out string? text, out _, out int? readError))
        {
            failure = ClipboardWriteResult.Failure(ClipboardWriteOutcome.NativeFailure, mutated: false, readError);
            return false;
        }

        byte[] bytes = Encoding.Unicode.GetBytes(text! + '\0');
        byteLength = bytes.Length;

        if (!_textNative.TryGlobalAlloc((nuint)byteLength, out hGlobal, out int allocError))
        {
            failure = ClipboardWriteResult.Failure(ClipboardWriteOutcome.NativeFailure, mutated: false, allocError);
            hGlobal = 0;
            return false;
        }

        if (!_textNative.TryGlobalLock(hGlobal, out nint pointer, out int lockError))
        {
            failure = ClipboardWriteResult.Failure(ClipboardWriteOutcome.NativeFailure, mutated: false, lockError);
            return false;
        }

        bool unlockOk;
        int unlockError = 0;
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
        }
        finally
        {
            unlockOk = _textNative.TryGlobalUnlock(hGlobal, out unlockError);
        }

        if (!unlockOk)
        {
            failure = ClipboardWriteResult.Failure(ClipboardWriteOutcome.NativeFailure, mutated: false, unlockError);
            return false;
        }

        failure = default;
        return true;
    }

    // BUG-006 GLOBAL_ROLLBACK_SLOT admission (write-side): the ONLY entry point that may grant
    // permission to create a rollback allocation. Two atomic attempts, never a spin/retry loop --
    // a miss just means "abort this attempt," leaving a later write (via BUG-002's own existing
    // bounded retry) to try again fresh:
    //   (1) Quarantined -> Reserved: if a rollback handle from a PRIOR catastrophic failure (this
    //       instance's own, or any other instance's -- the slot is process-wide) is still
    //       unresolved, try to become the exclusive owner who gets to attempt cleaning it up.
    //   (2) Empty -> Reserved: only if (1) did not apply, try a fresh reservation for this write's
    //       own rollback allocation.
    // Returning ReservedForCleanup means the caller now safely owns the ONLY reference permitted to
    // touch s_quarantinedHandle/s_quarantinedByteLength -- the CompareExchange that produced this
    // result is itself the memory-ordering fence that makes the subsequent PLAIN reads of those two
    // fields safe (the publishing thread also used an Interlocked write to set SlotQuarantined, so a
    // genuine happens-before edge exists; correctness never depends on a plain, non-Interlocked read
    // of s_slotState anywhere in this type).
    private static SlotAdmission TryAdmitWrite()
    {
        if (Interlocked.CompareExchange(ref s_slotState, SlotReserved, SlotQuarantined) == SlotQuarantined)
            return SlotAdmission.ReservedForCleanup;

        if (Interlocked.CompareExchange(ref s_slotState, SlotReserved, SlotEmpty) == SlotEmpty)
            return SlotAdmission.ReservedFresh;

        return SlotAdmission.Busy;
    }

    // BUG-006 (exception-atomic correction): one opportunistic resolution attempt for a
    // quarantined rollback handle at monitor shutdown. Reuses the IDENTICAL Quarantined -> Reserved
    // admission step a write uses -- a concurrent writer (this instance's own, impossible per the
    // single-owner-thread/message-loop-drained argument above, or a DIFFERENT instance's, in a
    // multi-monitor test scenario) that wins the same CompareExchange first simply leaves this
    // method nothing to do; this method never touches the handle unless IT wins that same exclusive
    // transition. Never a retry loop -- exactly one attempt, matching every other "best-effort, no
    // new subsystem" cleanup point in this design. EXCEPTION_ATOMICITY: this method's own required
    // try/finally state-repair shape (cleanup succeeds -> Reserved->Empty; cleanup does not ->
    // Reserved->Quarantined with the same coherent handle/byteLength still published) is already
    // fully provided by ResolveOrQuarantineRollbackHandle itself -- that method never throws and
    // always leaves the slot in one of exactly those two terminal states before returning, so this
    // call site needs no additional try/finally of its own. The outer caller (OwnerThreadMain) still
    // wraps this call in a defensive try/catch as a final backstop, even though nothing here can
    // actually throw by design -- mandatory listener/window/class teardown must never be skipped.
    private void TryResolveQuarantineAtShutdown()
    {
        if (Interlocked.CompareExchange(ref s_slotState, SlotReserved, SlotQuarantined) != SlotQuarantined)
            return;

        nint handle = s_quarantinedHandle;
        int byteLength = s_quarantinedByteLength;

        if (ResolveOrQuarantineRollbackHandle(handle, byteLength, out _))
            Interlocked.Exchange(ref s_slotState, SlotEmpty);
        // else: ResolveOrQuarantineRollbackHandle has already republished the SAME handle/byteLength
        // and transitioned back to SlotQuarantined itself -- nothing further for shutdown to do; no
        // new write follows shutdown, so there is no "abort a write" step here.
    }

    // BUG-006 CLEANUP (exception-atomic correction): attempts to fully resolve (free) a
    // PRIVON-owned rollback handle the caller already exclusively owns (via TryAdmitWrite's
    // ReservedForCleanup, this write's own catastrophic failure, or shutdown). GlobalFree's own
    // documented Microsoft contract: success returns NULL; failure returns the SAME, STILL-VALID
    // handle -- so a failed free never invalidates `handle`, and calling GlobalFree on it again
    // afterward is a legitimate retry, never a double-free. On a free failure, re-locks the SAME
    // handle and overwrites its content with exactly `byteLength` zero bytes before retrying the
    // free once. EXCEPTION_ATOMICITY: the scrub attempt (lock/zero-fill/unlock/retry-free) is
    // wrapped so that ANY managed exception during it (e.g. Marshal.Copy) is caught here and
    // treated identically to any other unresolved-cleanup outcome -- this method NEVER throws,
    // matching every other native-interaction Try* helper in this type, and its own `finally`
    // GUARANTEES the handle is published into the global quarantine slot whenever it could not be
    // freed, regardless of WHERE inside the attempt something failed or threw. The caller MUST NOT
    // touch `handle` again after this method returns false -- ownership has been transferred to the
    // slot. Returns true only when the handle has been genuinely freed (no residual ownership of any
    // kind remains).
    private bool ResolveOrQuarantineRollbackHandle(nint handle, int byteLength, out int? win32Error)
    {
        bool resolved = false;
        int? reportedError = null;

        // The ENTIRE resolution attempt -- including the very first, plain GlobalFree call, not
        // merely the scrub sub-block -- is inside this one try/catch/finally. An exception from
        // ANY step (the first free, the scrub's lock, the zero-fill Marshal.Copy, the scrub's
        // unlock, or the retry free) is caught identically; the finally below is what actually
        // makes the method's own doc claim true ("regardless of WHERE inside the attempt
        // something failed or threw").
        try
        {
            if (_textNative.TryGlobalFree(handle, out int freeError))
            {
                resolved = true;
            }
            else
            {
                reportedError = freeError;

                if (_textNative.TryGlobalLock(handle, out nint pointer, out _))
                {
                    try
                    {
                        byte[] zeros = new byte[byteLength];
                        Marshal.Copy(zeros, 0, pointer, byteLength);
                    }
                    finally
                    {
                        // Best-effort -- content is already zeroed (or the attempt was made)
                        // either way; an unlock failure here does not change whether the
                        // retry-free below is attempted.
                        _textNative.TryGlobalUnlock(handle, out _);
                    }

                    if (_textNative.TryGlobalFree(handle, out int retryFreeError))
                        resolved = true;
                    else
                        reportedError = retryFreeError;
                }
            }
        }
        catch
        {
            // An unexpected managed exception (e.g. the zero-fill Marshal.Copy, or the initial/
            // retry GlobalFree call itself) anywhere in this resolution attempt -- converted into
            // an ordinary unresolved-cleanup outcome for THIS boundary (every native-interaction
            // helper in this type reports failure via bool/out, never throws). The finally below
            // still guarantees the handle is quarantined, never silently lost, regardless of which
            // specific step failed or threw.
        }
        finally
        {
            if (!resolved)
            {
                // Still unresolved -- publish/republish into the global slot. The caller must
                // currently hold Reserved ownership; this transfers that ownership to the slot.
                // Runs even if the try block above is unwinding due to the caught exception, so
                // the handle can never fall out of scope unresolved.
                s_quarantinedHandle = handle;
                s_quarantinedByteLength = byteLength;
                Interlocked.Exchange(ref s_slotState, SlotQuarantined);
            }
        }

        win32Error = resolved ? null : reportedError;
        return resolved;
    }

    // GLOBALFREE_LEAK_VISIBILITY (Phase 3A.4 STEP4, resolved): frees a still-PRIVON-owned
    // hGlobal, and if the free itself fails, that failure ALWAYS wins over primaryOutcome/
    // primaryError -- reported as NativeFailure with the FREE's own Win32 error. A cleanup
    // failure is never silently hidden behind a more "expected" primary outcome (e.g.
    // SequenceChanged) -- no separate public leak flag exists; this is the only way a leak
    // becomes visible to the caller.
    private (ClipboardWriteOutcome Outcome, int? Win32Error) FreeOwnedHGlobal(
        nint hGlobal, ClipboardWriteOutcome primaryOutcome, int? primaryError)
    {
        if (!_textNative.TryGlobalFree(hGlobal, out int freeError))
            return (ClipboardWriteOutcome.NativeFailure, freeError);
        return (primaryOutcome, primaryError);
    }

    // SEQUENCE_CAS + FOREGROUND_WRITE_EXECUTION_GUARD's CHECK 2 + DESTRUCTIVE_BOUNDARY, entirely
    // inside the OpenClipboard/CloseClipboard bracket ExecuteWrite already established. Sequence
    // is checked FIRST (unchanged from STEP4, equality-only, no "+1" guessing), and only once it
    // matches does CHECK 2 (Phase 3A.5 STEP4) run -- a fresh foreground check immediately before
    // EmptyClipboard. Target and sequence are independent AND conditions: neither substitutes for
    // the other. The instant EmptyClipboard succeeds, `mutated` becomes true for every path from
    // here on, regardless of what happens next.
    //
    // BUG-006 fix (S2 CONFIRMED, closed) / EXCEPTION-ATOMIC CORRECTION: this layer used to never
    // cache original content for a rollback -- if EmptyClipboard succeeded and the protected
    // SetClipboardData then failed, the original content was permanently lost. It now reserves the
    // process-wide GLOBAL_ROLLBACK_SLOT (TryAdmitWrite) and FULLY prepares a rollback HGLOBAL
    // already containing the CURRENT content (TryPrepareRollbackFull) BEFORE EmptyClipboard is
    // ever called -- PRE_DESTRUCTIVE_ROLLBACK: no exception-capable RAW native copy remains after
    // crossing the destructive boundary. If the protected Set(B) then fails, restoring A is
    // attempted FIRST -- strictly before any cleanup of B -- so an exception/failure while freeing
    // B can never prevent the restoration attempt. Recovery success or failure both remain
    // ClipboardWriteOutcome.NativeFailure -- a recovery path must NEVER report protection Success;
    // VerifyWrite is only ever reached for a genuine Success below, so it is structurally never
    // invoked for a recovery outcome, and the self-write suppression marker
    // (_lastSuccessfulWriteSequence, armed only inside VerifyWrite) is therefore never armed for a
    // restore's own SetClipboardData either.
    //
    // EXCEPTION_ATOMIC_SLOT_OWNERSHIP: once this call transitions the global slot to Reserved
    // (fresh, or after resolving a prior quarantine on admission), it becomes the SOLE
    // exception-atomic owner responsible for producing exactly one terminal state -- Reserved ->
    // Empty or Reserved -> Quarantined -- before returning on EVERY path, including one that exits
    // via an unhandled exception. `rollbackPending` tracks whether a PRIVON-owned, fully-prepared
    // rollback handle currently exists and has not yet been resolved by normal code; the `finally`
    // block is the single, unconditional fallback that resolves it (via the SAME
    // ResolveOrQuarantineRollbackHandle used everywhere else, which itself never throws) whenever
    // normal code never got the chance to -- whether because of an early return this method's own
    // logic simply forgot to repair (there is none left, but the mechanism does not rely on that
    // being true forever) or because an exception propagated out of TryPrepareRollbackFull (e.g.
    // Marshal.Copy) before any explicit resolution ran. No return, no expected failure, and no
    // managed exception can leave the slot silently Reserved.
    private (ClipboardWriteOutcome Outcome, bool Mutated, uint WriteSequence, int? Win32Error) MutateWhileClipboardOpen(
        long writeId, uint expectedSequence, ForegroundTargetSnapshot? expectedTarget, nint hGlobal)
    {
        uint currentSequence = _native.GetClipboardSequenceNumber();

        // Phase 3C STEP41.2: casMatched is logically identical to the pre-existing condition below
        // (De Morgan's law: !(currentSequence == 0 || currentSequence != expectedSequence) ==
        // currentSequence != 0 && currentSequence == expectedSequence) -- computed once here so the
        // diagnostic event and the branch it gates can share exactly one boolean, never two
        // independently-written copies of the same comparison.
        bool casMatched = currentSequence != 0 && currentSequence == expectedSequence;
        RaiseWriteDiagnostic(new ClipboardWriteDiagnosticEvent(
            writeId, ClipboardWriteDiagnosticKind.CasSequenceObserved, null, currentSequence, casMatched, null, null, null));

        if (!casMatched)
        {
            var (freeOutcome, freeErr) = FreeOwnedHGlobal(hGlobal, ClipboardWriteOutcome.SequenceChanged, null);
            return (freeOutcome, false, 0, freeErr);
        }

        // CHECK 2 (Phase 3A.5 STEP4): only reached after the sequence gate already matched.
        if (expectedTarget is { } expected2)
        {
            var guardResult = CheckForegroundTarget(expected2);
            if (guardResult != ForegroundGuardResult.Matched)
            {
                var (freeOutcome, freeErr) = FreeOwnedHGlobal(hGlobal, MapWriteGuardOutcome(guardResult), null);
                return (freeOutcome, false, 0, freeErr);
            }
        }

        // BUG-006 GLOBAL_ROLLBACK_SLOT: reserve exclusive process-wide ownership BEFORE any rollback
        // allocation is ever created -- see TryAdmitWrite's own doc for why this ordering (not a
        // later CAS at cleanup time) is what actually prevents two writers from ever both holding a
        // RAW-bearing rollback handle at once. Every branch below that returns before reaching the
        // try/finally further down never allocated anything, so nothing further to resolve.
        var admission = TryAdmitWrite();

        if (admission == SlotAdmission.Busy)
        {
            // Another writer (this instance's own impossible by construction, or a different
            // instance's) currently holds the slot -- abort before any rollback allocation.
            var (freeOutcome, freeErr) = FreeOwnedHGlobal(hGlobal, ClipboardWriteOutcome.NativeFailure, null);
            return (freeOutcome, false, 0, freeErr);
        }

        if (admission == SlotAdmission.ReservedForCleanup)
        {
            // Safe to read plainly -- the CompareExchange that produced ReservedForCleanup is itself
            // the memory-ordering fence (see TryAdmitWrite's own doc). ResolveOrQuarantineRollbackHandle
            // never throws (see its own doc) -- this whole branch needs no try/finally of its own.
            nint quarantinedHandle = s_quarantinedHandle;
            int quarantinedByteLength = s_quarantinedByteLength;

            if (!ResolveOrQuarantineRollbackHandle(quarantinedHandle, quarantinedByteLength, out int? oldQuarantineError))
            {
                // Still unresolved -- already republished as Quarantined inside the helper; this
                // write's own ownership transfer is complete. Abort before any rollback allocation
                // of its own.
                var (freeOutcome, freeErr) = FreeOwnedHGlobal(hGlobal, ClipboardWriteOutcome.NativeFailure, oldQuarantineError);
                return (freeOutcome, false, 0, freeErr);
            }

            // Resolved -- no Empty round-trip to redo here. TryAdmitWrite's own CompareExchange
            // (Quarantined -> Reserved) already granted this call exclusive Reserved ownership
            // before ResolveOrQuarantineRollbackHandle was ever invoked; that method only ever
            // writes s_slotState on the NOT-resolved path (to publish Quarantined -- see its own
            // doc) -- on success it deliberately leaves slot ownership exactly where TryAdmitWrite
            // already put it. This SAME winner proceeds directly into its own new write below, with
            // no intervening yield -- unifying this branch with SlotAdmission.ReservedFresh from
            // this point on: both enter the try/finally below already holding Reserved.
        }

        // From here on this call exclusively owns the global rollback slot (Reserved) and is the
        // sole exception-atomic owner of resolving it -- see this method's own EXCEPTION_ATOMIC_
        // SLOT_OWNERSHIP doc above.
        nint rollbackHGlobal = 0;
        int rollbackByteLength = 0;
        bool rollbackPending = false;

        try
        {
            // PRE_DESTRUCTIVE_ROLLBACK: read + allocate + lock + copy + unlock A, FULLY, before
            // EmptyClipboard is ever called.
            //
            // EXCEPTION_ATOMIC_ALLOCATION_WINDOW: `hGlobal`/`byteLength` are `out` parameters --
            // true aliases to `rollbackHGlobal`/`rollbackByteLength` above -- so a successful
            // TryGlobalAlloc deep inside TryPrepareRollbackFull is visible here IMMEDIATELY, even
            // if a LATER step inside that same call (TryGlobalLock/Marshal.Copy/TryGlobalUnlock)
            // then throws before the call can return normally. Without this inner try/catch,
            // such a throw would skip BOTH assignment branches below (the `if (!...)` branch and
            // the `rollbackPending = true` line), leaving `rollbackPending` stuck at its pre-call
            // `false` while `rollbackHGlobal` already holds a real, still-unresolved handle -- the
            // outer `finally` would then never resolve it, silently leaking a PRIVON-owned,
            // possibly RAW-bearing allocation. Catching here does not suppress anything (the
            // exception is always rethrown unchanged) -- it only re-derives `rollbackPending` from
            // whatever `rollbackHGlobal` actually holds at the moment of the throw, exactly the
            // same derivation the normal `!prepared` branch already uses below.
            bool prepared;
            ClipboardWriteResult prepFailure;
            try
            {
                prepared = TryPrepareRollbackFull(out rollbackHGlobal, out rollbackByteLength, out prepFailure);
            }
            catch
            {
                rollbackPending = rollbackHGlobal != 0;
                throw;
            }

            if (!prepared)
            {
                // rollbackHGlobal may be 0 (allocation itself never succeeded -- nothing to
                // resolve) or non-zero (allocated, but lock/copy/unlock failed) -- either way,
                // `rollbackPending` correctly reflects which via the check below, and the
                // `finally` block resolves it uniformly.
                rollbackPending = rollbackHGlobal != 0;
                var (freeOutcome, freeErr) = FreeOwnedHGlobal(hGlobal, prepFailure.Outcome, prepFailure.Win32Error);
                return (freeOutcome, false, 0, freeErr);
            }
            rollbackPending = true; // fully prepared, PRIVON-owned, unlocked, ready either to be Set or resolved

            if (!_textNative.EmptyClipboard(out int emptyError))
            {
                // EmptyClipboard itself failed -- the clipboard was never actually touched. The
                // rollback buffer (already fully containing A) is still ours; resolve it through
                // the SAME mechanism as every other rollback handle in this type -- NEVER simply
                // release the slot to Empty while it remains PRIVON-owned (the exact defect this
                // correction closes).
                var resolved = ResolveOrQuarantineRollbackHandle(rollbackHGlobal, rollbackByteLength, out int? rbErr);
                rollbackPending = false;
                var (freeOutcome, freeErr) = FreeOwnedHGlobal(hGlobal, ClipboardWriteOutcome.NativeFailure, emptyError);
                if (!resolved)
                    return (ClipboardWriteOutcome.NativeFailure, false, 0, rbErr);
                return (freeOutcome, false, 0, freeErr);
            }

            // DESTRUCTIVE_BOUNDARY crossed: the original clipboard content is already gone -- but
            // a rollback handle already FULLY containing it exists, entirely within this same
            // OpenClipboard bracket.
            if (!_textNative.TrySetClipboardData(hGlobal, out int setError))
            {
                // B_FAILURE_ORDER: restoring the user's clipboard is the priority -- attempt
                // Set(A) FIRST, strictly before touching B's own cleanup, so a failure/exception
                // while freeing B can never prevent the restoration attempt. The out parameter is
                // populated either way (Win32 contract), so it is captured once and used only in
                // the failure branch below.
                bool restoreSucceeded = _textNative.TrySetClipboardData(rollbackHGlobal, out int restoreError);

                if (restoreSucceeded)
                {
                    // Original restored -- rollbackHGlobal is now system-owned. NEVER free it
                    // again, on any path, from this point on. Only now does B's own cleanup run.
                    rollbackPending = false;
                    var (freeOutcome, freeErr) = FreeOwnedHGlobal(hGlobal, ClipboardWriteOutcome.NativeFailure, setError);
                    return (freeOutcome, true, 0, freeErr);
                }
                else
                {
                    // S3: the restoration's own failure is the more recent, more diagnostically
                    // relevant reason -- it is the primary reported error unless the SUBSEQUENT
                    // rollback-cleanup attempt itself also fails, in which case that failure
                    // supersedes it (unchanged most-recent-failure-wins precedence). B's own
                    // cleanup result is deliberately never folded into this reported error -- B
                    // never contains RAW content, and its own leak is the same already-accepted,
                    // diagnostic-only, non-PII concern as everywhere else in this method (see
                    // NON_RAW_FREE_FAILURE below) -- the PRIMARY signal for this scenario must
                    // always be restoration status, not B's own bookkeeping.
                    var resolved = ResolveOrQuarantineRollbackHandle(rollbackHGlobal, rollbackByteLength, out int? cleanupError);
                    rollbackPending = false;
                    FreeOwnedHGlobal(hGlobal, ClipboardWriteOutcome.NativeFailure, setError); // B cleanup, result intentionally not surfaced
                    if (!resolved)
                        return (ClipboardWriteOutcome.NativeFailure, true, 0, cleanupError);
                    return (ClipboardWriteOutcome.NativeFailure, true, 0, restoreError);
                }
            }

            // Set(B) succeeded -- hGlobal is now system-owned. NEVER free it again, on any path,
            // from this point on (in this method, in ExecuteWrite, or in VerifyWrite). The
            // rollback handle was never needed; resolve it (PRIVON still owns it -- the system
            // never saw it). A failure to free it is still surfaced (GLOBALFREE_LEAK_VISIBILITY,
            // applied identically to every other handle in this method) even though the protected
            // write itself already succeeded -- see this method's own class doc's
            // NON_RAW_FREE_FAILURE note for why this uniform treatment is a deliberate, accepted
            // simplification rather than a defect.
            var rollbackResolved = ResolveOrQuarantineRollbackHandle(rollbackHGlobal, rollbackByteLength, out int? unusedFreeError);
            rollbackPending = false;
            if (!rollbackResolved)
                return (ClipboardWriteOutcome.NativeFailure, true, 0, unusedFreeError);

            uint writeSequence = _native.GetClipboardSequenceNumber();
            RaiseWriteDiagnostic(new ClipboardWriteDiagnosticEvent(
                writeId, ClipboardWriteDiagnosticKind.PostSetSequenceCaptured, null, writeSequence, null, null, null, null));
            return (ClipboardWriteOutcome.Success, true, writeSequence, null);
        }
        finally
        {
            // EXCEPTION_ATOMIC_SLOT_OWNERSHIP fallback: reached on every exit from the try block
            // above, including one propagating an exception. If normal code already resolved the
            // rollback handle (rollbackPending == false), this is a no-op resolution attempt.
            if (rollbackPending)
                ResolveOrQuarantineRollbackHandle(rollbackHGlobal, rollbackByteLength, out _);

            // Release Reserved -> Empty UNLESS a resolution above (just now, or earlier in the try
            // block) already transitioned it to Quarantined. Safe to read plainly: we are the
            // exclusive Reserved owner for the entire duration of this try/finally: no other
            // thread can be mutating s_slotState while we hold it.
            if (Volatile.Read(ref s_slotState) != SlotQuarantined)
                Interlocked.Exchange(ref s_slotState, SlotEmpty);
        }
    }

    // READBACK_VERIFICATION: a SEPARATE OpenClipboard/CloseClipboard bracket from the mutation
    // one -- reopened only after the mutation bracket's own Close already succeeded. hGlobal is
    // system-owned by this point and is never touched (freed or otherwise) anywhere in this
    // method.
    //
    // FINAL_VERIFICATION_SEQUENCE (Phase 3C STEP42, corrected): writeSequence (W, captured in
    // MutateWhileClipboardOpen immediately after SetClipboardData succeeded, while the mutation
    // bracket was still open) is passed in here PURELY as diagnostic/attribution context now -- it
    // is no longer required to equal the verification-reopen's own sequence (V, captured inside
    // VerifyWhileClipboardOpen below) for a write to be verified. See VerifyWhileClipboardOpen's
    // own doc for why: a real Windows + real ChatGPT manual trace proved W and V can legitimately
    // differ (multiple silent sequence-number bumps between the mutation Close and this
    // verification reopen, with no external content change at all) while the clipboard's actual,
    // externally-observable final content is still exactly what this write intended. Treating any
    // W-vs-V transition as an automatic failure (the pre-STEP42 behavior) is exactly the
    // MUTATED_WRITE_SUPERSEDED_BEFORE_MARKER defect this correction fixes -- see
    // VerifyWhileClipboardOpen's own class-level note for the corrected invariant.
    private ClipboardWriteResult VerifyWrite(long writeId, nint hwnd, uint writeSequence, string replacementText)
    {
        if (!_textNative.OpenClipboard(hwnd, out int openError))
        {
            // VERIFICATION_REOPEN_BUSY_NAMING: Busy is reused, not a new enum member -- only
            // ClipboardMutated (true here, since the mutation bracket already succeeded)
            // distinguishes this from the pre-mutation Busy case. No internal retry.
            return ClipboardWriteResult.Failure(ClipboardWriteOutcome.Busy, mutated: true, openError);
        }

        var outcome = VerifyWhileClipboardOpen(
            writeId, writeSequence, replacementText, out int? verifyError, out uint verificationSequence);

        if (!_textNative.CloseClipboard(out int closeError))
            return ClipboardWriteResult.Failure(ClipboardWriteOutcome.NativeFailure, mutated: true, closeError);

        if (outcome is ClipboardWriteOutcome.Superseded or ClipboardWriteOutcome.VerificationUnavailable)
        {
            // Superseded: a valid, but genuinely different, payload was read back -- someone
            // else's content is now on the clipboard, so there is nothing of OURS to report a
            // sequence for. VerificationUnavailable: the verification sequence itself was 0/
            // unreliable -- nothing was ever confirmed either way. Neither carries ResultSequence.
            return ClipboardWriteResult.Failure(outcome, mutated: true, verifyError);
        }

        if (outcome != ClipboardWriteOutcome.Success)
        {
            // WRITE_READBACK_MISMATCH_SEMANTICS (Phase 3A.4 STEP4.1, sequence source corrected by
            // STEP42): every NativeFailure/ReadBackMismatch outcome from VerifyWhileClipboardOpen
            // is reached only AFTER the verification sequence V was already confirmed reliable
            // (non-zero) -- so verificationSequence is still a meaningful, already-confirmed fact
            // and is reported as ResultSequence even on failure. This is V, not W -- see this
            // method's own class-level FINAL_VERIFICATION_SEQUENCE note.
            return ClipboardWriteResult.Failure(outcome, mutated: true, verifyError, verificationSequence);
        }

        // SELF_WRITE_MARKER_SEQUENCE (Phase 3C STEP42, corrected): the self-write suppression
        // marker -- and the ResultSequence reported to the caller -- are now the verification
        // sequence V (proven, in this SAME open verification session, to hold exactly
        // replacementText), not the intermediate post-Set capture W. Set only for a
        // fully-verified successful write (never for a merely-mutated-but-unverified one) -- only
        // ever touched from this owner thread.
        _lastSuccessfulWriteSequence = verificationSequence;
        return ClipboardWriteResult.Success(verificationSequence);
    }

    // FINAL_VERIFICATION_SEQUENCE / CURRENT_POST_SET_SEQUENCE_INVARIANT (Phase 3C STEP42,
    // corrected): a real Windows + real ChatGPT manual trace proved the PRE-STEP42 invariant --
    // "the verification-reopen sequence V must equal the post-Set capture W, or the write is
    // Superseded" -- is invalid in production. W is an INTERMEDIATE observation, captured while
    // the mutation bracket was still open; it is not a documented stable identity for the final,
    // externally-observable clipboard state once CloseClipboard has actually run (the OS can, and
    // in that trace did, bump the sequence counter one or more further times -- 3847 -> 3850 --
    // with no external content change at all). The corrected invariant established below no
    // longer gates the read-back comparison on W == V: it instead establishes ONE coherent
    // verified final state from a SINGLE open verification session -- obtain V, require it
    // reliable, confirm the format is present, read the text, and compare it ordinally against
    // replacementText. Only exact content equality -- never sequence equality alone -- determines
    // success. W remains available only as diagnostic/attribution context (see
    // ClipboardWriteDiagnosticKind.PostSetSequenceCaptured's own doc) and is no longer a
    // prerequisite for anything below.
    //
    // WRITE_READBACK_MISMATCH_SEMANTICS (Phase 3A.4 STEP4.1, still true, now unconditional on W
    // vs V): ReadBackMismatch is reserved for "a valid CF_UNICODETEXT payload was actually parsed
    // into a string, and that string is ordinally different from what was written," while V still
    // equals W (i.e. no evidence of an intervening sequence transition). Every other verification
    // failure -- format missing, malformed/unparseable payload, a genuine Win32 call failure -- is
    // reported as NativeFailure instead, because none of those cases ever produced a real string
    // to compare against replacementText in the first place. When a valid payload IS produced, IS
    // genuinely different from replacementText, AND V no longer equals W, that is reported as
    // Superseded instead of ReadBackMismatch -- the same "content differs" fact, but now with
    // positive evidence (a real sequence transition) that a different actor is responsible for it
    // (see this method's own SUPERSEDE_SEQUENCE_PROVENANCE-adjacent reasoning above).
    private ClipboardWriteOutcome VerifyWhileClipboardOpen(
        long writeId, uint writeSequence, string replacementText, out int? win32Error, out uint verificationSequence)
    {
        win32Error = null;

        uint currentSequence = _native.GetClipboardSequenceNumber();
        verificationSequence = currentSequence;

        // Phase 3C STEP42: sequenceMatchedPostSet is now DIAGNOSTIC/ATTRIBUTION context only --
        // used solely to choose between ReadBackMismatch and Superseded when the content
        // comparison below actually fails. It no longer gates whether that comparison is even
        // attempted (that is exactly the defect this correction fixes).
        bool sequenceMatchedPostSet = currentSequence == writeSequence;
        RaiseWriteDiagnostic(new ClipboardWriteDiagnosticEvent(
            writeId, ClipboardWriteDiagnosticKind.VerificationSequenceObserved, null, currentSequence, sequenceMatchedPostSet, null, null, null));

        // A verification sequence of 0 is never a trustworthy basis for ANY decision here --
        // there is no reliable marker to attribute a verified state to, no matter what the
        // read-back below might otherwise show. Matches the "0 is never a trustworthy compare
        // baseline" rule already applied to expectedSequence/the CAS gate elsewhere in this type.
        if (currentSequence == 0)
            return ClipboardWriteOutcome.VerificationUnavailable;

        // FORMAT_MISSING_CLASSIFICATION: no text at all to compare -- reached regardless of
        // whether V still equals W. No native call actually failed here either, so there is no
        // meaningful Win32 error code to report (win32Error stays null) -- this is a contract-
        // level anomaly, not an OS error.
        if (!_native.IsUnicodeTextAvailable())
            return ClipboardWriteOutcome.NativeFailure;

        // Phase 3C STEP41.2/STEP42: fired whenever V is reliable AND the format is still
        // available -- immediately before the actual read-back call, regardless of whether V
        // still equals W. Presence of this event is itself "read-back was attempted"; see
        // ClipboardWriteDiagnosticKind.ReadBackAttempted's own doc.
        RaiseWriteDiagnostic(new ClipboardWriteDiagnosticEvent(
            writeId, ClipboardWriteDiagnosticKind.ReadBackAttempted, null, null, null, null, null, null));

        if (!TryReadUnicodeTextBody(out string? text, out ClipboardReadOutcome failureOutcome, out int? readError))
        {
            // MALFORMED_READBACK_CLASSIFICATION: whether the failure was a genuine Win32 call
            // failure or MalformedData (odd size / missing NUL / etc.), no valid string was ever
            // produced -- neither case can be a "content differs" ReadBackMismatch/Superseded. A
            // real Win32 failure keeps its own error code; MalformedData has none to report. No
            // ReadBackExactMatch event either -- no valid string was ever produced to compare (see
            // ClipboardWriteDiagnosticKind.ReadBackExactMatch's own doc).
            win32Error = failureOutcome == ClipboardReadOutcome.NativeFailure ? readError : null;
            return ClipboardWriteOutcome.NativeFailure;
        }

        // EXACT_READBACK_RULE (Phase 3C STEP42): sequence and text are both read from the SAME
        // open verification session -- this ordinal comparison, not the W-vs-V sequence
        // transition, is what actually determines success. A reliable V whose read-back text
        // exactly matches replacementText is verified as OUR OWN write EVEN IF V differs from the
        // intermediate post-Set capture W.
        bool exactMatch = string.Equals(text, replacementText, StringComparison.Ordinal);
        RaiseWriteDiagnostic(new ClipboardWriteDiagnosticEvent(
            writeId, ClipboardWriteDiagnosticKind.ReadBackExactMatch, null, null, null, exactMatch, null, null));

        if (exactMatch)
            return ClipboardWriteOutcome.Success;

        // Content genuinely differs from what was intended -- never Success, never a self-write
        // marker, either way. sequenceMatchedPostSet distinguishes WHY: if V still equals W, this
        // is our own confirmed state somehow holding different text (ReadBackMismatch, unchanged
        // meaning from before this correction). If V has since moved on, a real sequence
        // transition is positive evidence a different actor's content is now on the clipboard
        // (Superseded) -- a genuine external replacement, which must still fail (see this
        // method's own class-level note).
        return sequenceMatchedPostSet ? ClipboardWriteOutcome.ReadBackMismatch : ClipboardWriteOutcome.Superseded;
    }

    // Deliberately NOT public, NOT even named after any product/policy concept -- this is a pure
    // three-way mechanical classification, produced by and consumed only within this type.
    private enum ForegroundGuardResult { Unavailable, Changed, Matched }

    // FOREGROUND_EXECUTION_GUARD core (Phase 3A.5 STEP4): fresh, uncached, on the owner thread
    // every time it is called -- GetForegroundWindow -> GetWindowThreadProcessId -> process-name
    // lookup, exactly the same three mechanical facts ForegroundTargetInspector.Capture()
    // produces (this type calls IForegroundTargetSource directly rather than composing through
    // ForegroundTargetInspector, since a guard needs an EQUALITY check against an already-known
    // expected value, not just a capture -- consistent with how this type already holds its other
    // native seams, _native/_textNative, directly rather than through intermediate wrapper
    // objects). Comparison is PID equality AND ordinal-case-insensitive process-name equality --
    // nothing else. This type does not know "ChatGPT" or any other product policy name; it only
    // ever compares two opaque (PID, name) facts for equality.
    //
    // FOREGROUND_GUARD_RESIDUAL_RACE: this check cannot be made atomic with the clipboard native
    // call that follows it -- no Windows API provides a joint foreground-identity-plus-clipboard-
    // access transaction. The guarantee this type provides is narrower and achievable: the check
    // runs on the SAME owner thread as the clipboard operation it gates, immediately adjacent to
    // that operation, with no intentional yield in between. See CHECK 1/CHECK 2 call sites for
    // exactly how "immediately adjacent" is realized for reads and writes respectively.
    // BUG-004 Gate 2H.3 (E+): obtains a FRESH coherent current-foreground identity through the one
    // shared ForegroundIdentityCapture primitive -- never by composing the sequence locally, which
    // is how this guard previously drifted to a PID+ProcessName-only comparison (BUG004-TOCTOU-001).
    // The comparison now covers every fact that participated in authorization, including the package
    // identity, so a previously-authorized product authorization can no longer cross onto a
    // different process that merely shares the PID and process name.
    //
    // FACTS_ONLY: compares CURRENT against the caller's EXPECTED snapshot. No supported package
    // family name (or any other product constant) exists anywhere in this assembly.
    private ForegroundGuardResult CheckForegroundTarget(ForegroundTargetSnapshot expected)
    {
        if (!ForegroundIdentityCapture.TryCapture(_foregroundSource, out var current))
            return ForegroundGuardResult.Unavailable;

        return ForegroundIdentityCapture.Matches(current, expected)
            ? ForegroundGuardResult.Matched
            : ForegroundGuardResult.Changed;
    }

    private static ClipboardReadOutcome MapReadGuardOutcome(ForegroundGuardResult result) => result switch
    {
        ForegroundGuardResult.Unavailable => ClipboardReadOutcome.TargetUnavailable,
        ForegroundGuardResult.Changed => ClipboardReadOutcome.TargetChanged,
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Matched must never reach a failure mapping."),
    };

    private static ClipboardWriteOutcome MapWriteGuardOutcome(ForegroundGuardResult result) => result switch
    {
        ForegroundGuardResult.Unavailable => ClipboardWriteOutcome.TargetUnavailable,
        ForegroundGuardResult.Changed => ClipboardWriteOutcome.TargetChanged,
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Matched must never reach a failure mapping."),
    };
}
