using System.Reflection;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Phase 3A.4 STEP2 -- Clipboard Change Monitor Foundation regression. All tests here use
// FakeClipboardMonitorNative (synthetic, OS-free) -- no real Windows window/clipboard resource
// is touched by anything in this file. See ClipboardChangeMonitorWindowsSmokeTests.cs for the
// one real-Win32 smoke test.
public class ClipboardChangeMonitorTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    // ---- 1/16. Start registers the listener exactly once; Remove occurs before DestroyWindow ----
    [Fact]
    public void Start_RegistersListenerOnce_AndOrdersCleanupCorrectly()
    {
        var native = new FakeClipboardMonitorNative();
        var monitor = new ClipboardChangeMonitor(native);

        monitor.Start();
        Assert.Equal(1, native.CallLog.Count(n => n == nameof(FakeClipboardMonitorNative.AddClipboardFormatListener)));

        monitor.Stop();

        Assert.Equal(
            [
                nameof(FakeClipboardMonitorNative.RegisterWindowClass),
                nameof(FakeClipboardMonitorNative.CreateMessageOnlyWindow),
                nameof(FakeClipboardMonitorNative.AddClipboardFormatListener),
                nameof(FakeClipboardMonitorNative.RemoveClipboardFormatListener),
                nameof(FakeClipboardMonitorNative.DestroyWindow),
                nameof(FakeClipboardMonitorNative.UnregisterWindowClass),
            ],
            native.CallLog);
    }

    // ---- 2. Stop unregisters the listener exactly once ----
    [Fact]
    public void Stop_UnregistersListenerOnce()
    {
        var native = new FakeClipboardMonitorNative();
        var monitor = new ClipboardChangeMonitor(native);

        monitor.Start();
        monitor.Stop();

        Assert.Equal(1, native.CallLog.Count(n => n == nameof(FakeClipboardMonitorNative.RemoveClipboardFormatListener)));
    }

    // ---- 3. Start -> Stop normal lifecycle ----
    [Fact]
    public void StartThenStop_CompletesWithoutException()
    {
        var native = new FakeClipboardMonitorNative();
        var monitor = new ClipboardChangeMonitor(native);

        monitor.Start();
        monitor.Stop();
    }

    // ---- 4. Dispose is idempotent, including before Start and after a failed Start ----
    [Fact]
    public void Dispose_IsIdempotent_AfterNormalLifecycle()
    {
        var native = new FakeClipboardMonitorNative();
        var monitor = new ClipboardChangeMonitor(native);
        monitor.Start();
        monitor.Stop();

        monitor.Dispose();
        monitor.Dispose();
    }

    [Fact]
    public void Dispose_IsSafe_BeforeStart()
    {
        var monitor = new ClipboardChangeMonitor(new FakeClipboardMonitorNative());
        monitor.Dispose();
    }

    [Fact]
    public void Dispose_IsSafe_AfterFailedStart()
    {
        var native = new FakeClipboardMonitorNative { AddListenerResult = false, AddListenerError = 5 };
        var monitor = new ClipboardChangeMonitor(native);

        Assert.Throws<InvalidOperationException>(monitor.Start);

        monitor.Dispose();
        monitor.Dispose();
    }

    // ---- 5. Stop is idempotent ----
    [Fact]
    public void Stop_IsIdempotent()
    {
        var native = new FakeClipboardMonitorNative();
        var monitor = new ClipboardChangeMonitor(native);
        monitor.Start();

        monitor.Stop();
        monitor.Stop();

        Assert.Equal(1, native.CallLog.Count(n => n == nameof(FakeClipboardMonitorNative.RemoveClipboardFormatListener)));
    }

    [Fact]
    public void Stop_IsSafe_BeforeStart()
    {
        var monitor = new ClipboardChangeMonitor(new FakeClipboardMonitorNative());
        monitor.Stop();
    }

    // ---- 6. Partial startup failure cleans up only what actually succeeded ----
    [Fact]
    public void PartialStartupFailure_AddListenerFails_DoesNotCallRemoveListener_ButStillDestroysWindowAndClass()
    {
        var native = new FakeClipboardMonitorNative { AddListenerResult = false, AddListenerError = 5 };
        var monitor = new ClipboardChangeMonitor(native);

        Assert.Throws<InvalidOperationException>(monitor.Start);

        Assert.DoesNotContain(nameof(FakeClipboardMonitorNative.RemoveClipboardFormatListener), native.CallLog);
        Assert.Contains(nameof(FakeClipboardMonitorNative.DestroyWindow), native.CallLog);
        Assert.Contains(nameof(FakeClipboardMonitorNative.UnregisterWindowClass), native.CallLog);
    }

    [Fact]
    public void PartialStartupFailure_WindowCreationFails_DoesNotCallDestroyWindowOrListenerCalls_ButUnregistersClass()
    {
        var native = new FakeClipboardMonitorNative { CreateWindowResult = false, CreateWindowError = 8 };
        var monitor = new ClipboardChangeMonitor(native);

        Assert.Throws<InvalidOperationException>(monitor.Start);

        Assert.DoesNotContain(nameof(FakeClipboardMonitorNative.DestroyWindow), native.CallLog);
        Assert.DoesNotContain(nameof(FakeClipboardMonitorNative.AddClipboardFormatListener), native.CallLog);
        Assert.DoesNotContain(nameof(FakeClipboardMonitorNative.RemoveClipboardFormatListener), native.CallLog);
        Assert.Contains(nameof(FakeClipboardMonitorNative.UnregisterWindowClass), native.CallLog);
    }

    // ---- 7. HWND creation failure fails closed (Start throws, carries the Win32 error) ----
    [Fact]
    public void HwndCreationFailure_StartThrows_WithWin32ErrorInMessage()
    {
        var native = new FakeClipboardMonitorNative { CreateWindowResult = false, CreateWindowError = 8 };
        var monitor = new ClipboardChangeMonitor(native);

        var ex = Assert.Throws<InvalidOperationException>(monitor.Start);
        Assert.Contains("8", ex.Message);
    }

    // ---- 8. AddClipboardFormatListener failure fails closed ----
    [Fact]
    public void AddListenerFailure_StartThrows_WithWin32ErrorInMessage()
    {
        var native = new FakeClipboardMonitorNative { AddListenerResult = false, AddListenerError = 1418 };
        var monitor = new ClipboardChangeMonitor(native);

        var ex = Assert.Throws<InvalidOperationException>(monitor.Start);
        Assert.Contains("1418", ex.Message);
    }

    // ---- 9/10. WM_CLIPBOARDUPDATE forwards sequence number and Unicode-text availability ----
    [Fact]
    public void ClipboardUpdate_ForwardsSequenceNumberAndUnicodeTextAvailability()
    {
        var native = new FakeClipboardMonitorNative { SequenceNumber = 42, UnicodeTextAvailable = true };
        var monitor = new ClipboardChangeMonitor(native);
        var received = new ManualResetEventSlim(false);
        ClipboardChangeNotification? notification = null;

        monitor.Changed += (_, n) => { notification = n; received.Set(); };
        monitor.Start();

        native.RaiseClipboardUpdate();
        Assert.True(received.Wait(WaitTimeout));

        monitor.Stop();

        Assert.NotNull(notification);
        Assert.Equal(42u, notification!.Value.SequenceNumber);
        Assert.True(notification.Value.HasUnicodeText);
    }

    [Fact]
    public void ClipboardUpdate_NoUnicodeText_ForwardsFalse()
    {
        var native = new FakeClipboardMonitorNative { SequenceNumber = 7, UnicodeTextAvailable = false };
        var monitor = new ClipboardChangeMonitor(native);
        var received = new ManualResetEventSlim(false);
        ClipboardChangeNotification? notification = null;

        monitor.Changed += (_, n) => { notification = n; received.Set(); };
        monitor.Start();

        native.RaiseClipboardUpdate();
        Assert.True(received.Wait(WaitTimeout));

        monitor.Stop();

        Assert.False(notification!.Value.HasUnicodeText);
    }

    // ---- 11. sequence 0 -> HasReliableSequence = false ----
    [Fact]
    public void ZeroSequence_HasReliableSequenceIsFalse()
    {
        var native = new FakeClipboardMonitorNative { SequenceNumber = 0 };
        var monitor = new ClipboardChangeMonitor(native);
        var received = new ManualResetEventSlim(false);
        ClipboardChangeNotification? notification = null;

        monitor.Changed += (_, n) => { notification = n; received.Set(); };
        monitor.Start();

        native.RaiseClipboardUpdate();
        Assert.True(received.Wait(WaitTimeout));

        monitor.Stop();

        Assert.Equal(0u, notification!.Value.SequenceNumber);
        Assert.False(notification.Value.HasReliableSequence);
    }

    // ---- 12. nonzero sequence -> HasReliableSequence = true ----
    [Fact]
    public void NonZeroSequence_HasReliableSequenceIsTrue()
    {
        var native = new FakeClipboardMonitorNative { SequenceNumber = 1 };
        var monitor = new ClipboardChangeMonitor(native);
        var received = new ManualResetEventSlim(false);
        ClipboardChangeNotification? notification = null;

        monitor.Changed += (_, n) => { notification = n; received.Set(); };
        monitor.Start();

        native.RaiseClipboardUpdate();
        Assert.True(received.Wait(WaitTimeout));

        monitor.Stop();

        Assert.True(notification!.Value.HasReliableSequence);
    }

    // ---- 13. Notification carries no clipboard-text-shaped field ----
    [Fact]
    public void Notification_HasExactlyTheThreeMetadataFields_NoStringMember()
    {
        var properties = typeof(ClipboardChangeNotification)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "HasReliableSequence", "HasUnicodeText", "SequenceNumber" },
            properties);
        Assert.DoesNotContain(
            typeof(ClipboardChangeNotification).GetProperties(),
            p => p.PropertyType == typeof(string));
    }

    // ---- 14. Subscriber exception never corrupts native resource cleanup ----
    [Fact]
    public void SubscriberException_DoesNotPreventCleanupOrCorruptOrdering()
    {
        var native = new FakeClipboardMonitorNative();
        var monitor = new ClipboardChangeMonitor(native);
        var handled = new ManualResetEventSlim(false);

        monitor.Changed += (_, _) =>
        {
            try { throw new InvalidOperationException("synthetic subscriber failure"); }
            finally { handled.Set(); }
        };

        monitor.Start();
        native.RaiseClipboardUpdate();
        Assert.True(handled.Wait(WaitTimeout));

        // The owner thread's message loop must still be alive and able to shut down cleanly
        // after a subscriber threw -- Stop() must not throw, and cleanup order must still hold.
        monitor.Stop();

        Assert.Equal(
            [
                nameof(FakeClipboardMonitorNative.RegisterWindowClass),
                nameof(FakeClipboardMonitorNative.CreateMessageOnlyWindow),
                nameof(FakeClipboardMonitorNative.AddClipboardFormatListener),
                nameof(FakeClipboardMonitorNative.GetClipboardSequenceNumber),
                nameof(FakeClipboardMonitorNative.IsUnicodeTextAvailable),
                nameof(FakeClipboardMonitorNative.RemoveClipboardFormatListener),
                nameof(FakeClipboardMonitorNative.DestroyWindow),
                nameof(FakeClipboardMonitorNative.UnregisterWindowClass),
            ],
            native.CallLog);
    }

    // ---- 15. Window creation and destruction occur on the same (owner) thread ----
    [Fact]
    public void WindowLifecycle_RegisterAndDestroy_OccurOnSameOwnerThread_NotTheCallingThread()
    {
        var native = new FakeClipboardMonitorNative();
        var monitor = new ClipboardChangeMonitor(native);
        int callingThreadId = Environment.CurrentManagedThreadId;

        monitor.Start();
        monitor.Stop();

        Assert.NotNull(native.RegisterWindowClassThreadId);
        Assert.NotNull(native.DestroyWindowThreadId);
        Assert.Equal(native.RegisterWindowClassThreadId, native.DestroyWindowThreadId);
        Assert.NotEqual(callingThreadId, native.RegisterWindowClassThreadId);
    }

    // ---- 17. Repeated Start is rejected (chosen policy: reject, not idempotent) ----
    [Fact]
    public void SecondStart_Throws_EvenAfterSuccessfulFirstStart()
    {
        var native = new FakeClipboardMonitorNative();
        var monitor = new ClipboardChangeMonitor(native);

        monitor.Start();
        Assert.Throws<InvalidOperationException>(monitor.Start);

        monitor.Stop();
    }

    [Fact]
    public void SecondStart_Throws_AfterFailedFirstStart()
    {
        var native = new FakeClipboardMonitorNative { AddListenerResult = false, AddListenerError = 5 };
        var monitor = new ClipboardChangeMonitor(native);

        Assert.Throws<InvalidOperationException>(monitor.Start);
        Assert.Throws<InvalidOperationException>(monitor.Start);
    }

    // ---- STEP2.1 hardening: Stop timeout is surfaced, never silently swallowed ----
    [Fact]
    public void Stop_Timeout_SurfacesFailure()
    {
        var native = new FakeClipboardMonitorNative { IgnoreShutdown = true };
        var monitor = new ClipboardChangeMonitor(native, TimeSpan.FromMilliseconds(50));
        monitor.Start();

        var ex = Assert.Throws<InvalidOperationException>(monitor.Stop);
        Assert.Contains("timeout", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Unstick the owner thread so it doesn't stay permanently blocked for the rest of the
        // test run -- this is test hygiene, not part of the behavior under test.
        native.ForceShutdown();
    }

    // ---- STEP2.1 hardening: Dispose does not swallow a cleanup failure ----
    [Fact]
    public void Dispose_Timeout_SurfacesFailure_DoesNotSwallow()
    {
        var native = new FakeClipboardMonitorNative { IgnoreShutdown = true };
        var monitor = new ClipboardChangeMonitor(native, TimeSpan.FromMilliseconds(50));
        monitor.Start();

        Assert.Throws<InvalidOperationException>(monitor.Dispose);

        native.ForceShutdown();
    }

    // ---- STEP2.1 hardening: a failed Dispose never falsely marks the instance as fully
    // cleaned up -- a later retry genuinely retries cleanup instead of silently no-op'ing ----
    [Fact]
    public void Dispose_AfterTimeoutFailure_DoesNotMarkFalselyDisposed_LaterRetrySucceeds()
    {
        var native = new FakeClipboardMonitorNative { IgnoreShutdown = true };
        var monitor = new ClipboardChangeMonitor(native, TimeSpan.FromMilliseconds(50));
        monitor.Start();

        Assert.Throws<InvalidOperationException>(monitor.Dispose);

        // The owner thread becomes responsive again (simulating recovery) -- a genuine retry,
        // not a silent no-op, must now be able to complete cleanup successfully.
        native.IgnoreShutdown = false;
        native.ForceShutdown();

        var ex = Record.Exception(monitor.Dispose);
        Assert.Null(ex);
        Assert.Equal(1, native.CallLog.Count(n => n == nameof(FakeClipboardMonitorNative.RemoveClipboardFormatListener)));
    }

    // ---- STEP2.1: successful Stop followed by Dispose performs no duplicate native teardown ----
    [Fact]
    public void SuccessfulStop_ThenDispose_PerformsNoDuplicateNativeTeardown()
    {
        var native = new FakeClipboardMonitorNative();
        var monitor = new ClipboardChangeMonitor(native);

        monitor.Start();
        monitor.Stop();
        monitor.Dispose();

        Assert.Equal(1, native.CallLog.Count(n => n == nameof(FakeClipboardMonitorNative.RemoveClipboardFormatListener)));
        Assert.Equal(1, native.CallLog.Count(n => n == nameof(FakeClipboardMonitorNative.DestroyWindow)));
        Assert.Equal(1, native.CallLog.Count(n => n == nameof(FakeClipboardMonitorNative.UnregisterWindowClass)));
    }

    // ---- STEP2.1: CALLBACK_THREAD_CONTRACT -- Changed is raised on the same owner thread that
    // owns window-class registration/destruction, never the caller's thread ----
    [Fact]
    public void Changed_IsInvokedOnOwnerThread_SameThreadAsWindowLifecycle()
    {
        var native = new FakeClipboardMonitorNative { SequenceNumber = 1 };
        var monitor = new ClipboardChangeMonitor(native);
        var received = new ManualResetEventSlim(false);
        int callingThreadId = Environment.CurrentManagedThreadId;
        int? callbackThreadId = null;

        monitor.Changed += (_, _) => { callbackThreadId = Environment.CurrentManagedThreadId; received.Set(); };
        monitor.Start();

        native.RaiseClipboardUpdate();
        Assert.True(received.Wait(WaitTimeout));

        monitor.Stop();

        Assert.NotEqual(callingThreadId, callbackThreadId);
        Assert.Equal(native.RegisterWindowClassThreadId, callbackThreadId);
    }

    // ---- 18. No raw/synthetic clipboard string logging: structural proxy scoped to the
    // change-notification surface specifically. As of Phase 3A.4 STEP3, ReadTextSnapshotAsync
    // deliberately DOES expose clipboard text (ClipboardTextSnapshot.Text) -- that capability is
    // the explicit point of STEP3, so a blanket "no string anywhere in the assembly" assertion
    // is no longer the correct invariant. What must still hold, unconditionally, is that the
    // passive CHANGE NOTIFICATION path (Changed/ClipboardChangeNotification) never carries text
    // -- see ClipboardChangeNotification's own doc. ----
    [Fact]
    public void ChangeNotification_PublicSurface_StillHasNoStringMembers()
    {
        var stringMembers = typeof(ClipboardChangeNotification)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m =>
                (m is PropertyInfo p && p.PropertyType == typeof(string)) ||
                (m is FieldInfo f && f.FieldType == typeof(string)));

        Assert.Empty(stringMembers);
    }

    // No raw/synthetic clipboard text ever appears in an exception message or Win32-error-only
    // failure metadata anywhere in this file's other tests -- enforced by construction (every
    // Assert.Contains/DoesNotContain against exception messages in this suite checks only
    // numeric error codes and operation names, never text), and directly for the read failure
    // path in ClipboardChangeMonitorReadTests.cs.
}
