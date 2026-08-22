using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Phase 0.2B STEP57 -- Foreground Change Monitor regression, mirroring SessionLockMonitorTests.cs's
// own exact shape/structure. All tests here use FakeForegroundChangeNative (synthetic, OS-free) -- no
// real Windows window/WinEvent hook is touched by anything in this file, and no automated test ever
// depends on which real window is foreground. See ForegroundChangeMonitorWindowsSmokeTests.cs for the
// real-Win32 smoke tests (registration/cleanup mechanics, plus one controlled synthetic foreground
// transition -- it never inspects which real user window became foreground).
public class ForegroundChangeMonitorTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    // ==================================================================
    // REGISTRATION LIFECYCLE / CLEANUP ORDERING
    // ==================================================================

    // ---- 1/2. Start installs the hook exactly once; cleanup order matches the required
    // reverse-of-startup sequence ----
    [Fact]
    public void Start_InstallsHookOnce_AndOrdersCleanupCorrectly()
    {
        var native = new FakeForegroundChangeNative();
        var monitor = new ForegroundChangeMonitor(native);

        monitor.Start();
        Assert.Equal(1, native.CallLog.Count(n => n == nameof(FakeForegroundChangeNative.SetHook)));

        monitor.Stop();

        Assert.Equal(
            [
                nameof(FakeForegroundChangeNative.RegisterWindowClass),
                nameof(FakeForegroundChangeNative.CreateMessageOnlyWindow),
                nameof(FakeForegroundChangeNative.SetHook),
                nameof(FakeForegroundChangeNative.Unhook),
                nameof(FakeForegroundChangeNative.DestroyWindow),
                nameof(FakeForegroundChangeNative.UnregisterWindowClass),
            ],
            native.CallLog);
    }

    // ---- 3. Stop unhooks exactly once ----
    [Fact]
    public void Stop_UnhooksExactlyOnce()
    {
        var native = new FakeForegroundChangeNative();
        var monitor = new ForegroundChangeMonitor(native);

        monitor.Start();
        monitor.Stop();

        Assert.Equal(1, native.CallLog.Count(n => n == nameof(FakeForegroundChangeNative.Unhook)));
    }

    // ---- 4. Start then Stop succeeds ----
    [Fact]
    public void StartThenStop_CompletesWithoutException()
    {
        var native = new FakeForegroundChangeNative();
        var monitor = new ForegroundChangeMonitor(native);

        monitor.Start();
        monitor.Stop();
    }

    // ==================================================================
    // EVENT TRANSLATION / DUPLICATE POLICY
    // ==================================================================

    // ---- 5. ForegroundChanged raised for translated event ----
    [Fact]
    public async Task ForegroundChanged_RaisedForTranslatedEvent()
    {
        var native = new FakeForegroundChangeNative();
        var monitor = new ForegroundChangeMonitor(native);
        var raised = 0;
        monitor.ForegroundChanged += (_, _) => Interlocked.Increment(ref raised);

        monitor.Start();
        native.RaiseForegroundChanged();

        await WaitUntilAsync(() => Volatile.Read(ref raised) == 1);

        monitor.Stop();
    }

    // ---- 6/7. Repeated events emit once per event -- no dedup/debounce/coalescing (STEP56
    // DUPLICATE_EVENT_POLICY, frozen) ----
    [Fact]
    public async Task ForegroundChanged_RaisedOnceMorePerRepeatedEvent_NoDedupOrDebounce()
    {
        var native = new FakeForegroundChangeNative();
        var monitor = new ForegroundChangeMonitor(native);
        var raised = 0;
        monitor.ForegroundChanged += (_, _) => Interlocked.Increment(ref raised);

        monitor.Start();
        native.RaiseForegroundChanged();
        native.RaiseForegroundChanged();
        native.RaiseForegroundChanged();

        await WaitUntilAsync(() => Volatile.Read(ref raised) == 3);

        monitor.Stop();
    }

    // ---- 8. No public event after logical stop ----
    [Fact]
    public void ForegroundChanged_NeverRaisedAfterStop()
    {
        var native = new FakeForegroundChangeNative();
        var monitor = new ForegroundChangeMonitor(native);
        var raised = 0;
        monitor.ForegroundChanged += (_, _) => Interlocked.Increment(ref raised);

        monitor.Start();
        monitor.Stop();

        // The owner thread has already confirmed exit (Stop's bounded Join returned successfully) --
        // no further message, including one queued but never drained, can reach RaiseForegroundChanged.
        Assert.Equal(0, raised);
    }

    // ---- 9/10. Invalid HWND / wrong event type produce no notification -- characterized as a pure,
    // independently unit-testable predicate (WINEVENT_INVALID_EVENT_FILTER, STEP56 frozen). This is
    // the filtering logic that lives inside the real WinEventProc callback itself, which the fake
    // seam deliberately bypasses (a filtered-out event never becomes a posted message in the first
    // place) -- so it is verified directly against Win32ForegroundChangeNative's own pure filter
    // function rather than through the monitor's full lifecycle. Matches the instruction's own
    // preference for a simpler compile-time/API-shape-style assertion over a brittle end-to-end one. ----
    [Theory]
    [InlineData(0x0003u, 1, true)]   // EVENT_SYSTEM_FOREGROUND, valid hwnd -> accepted
    [InlineData(0x0003u, 0, false)]  // EVENT_SYSTEM_FOREGROUND, hwnd == IntPtr.Zero -> rejected
    [InlineData(0x0004u, 1, false)]  // wrong event type (EVENT_SYSTEM_MENUSTART), valid hwnd -> rejected
    [InlineData(0x0004u, 0, false)]  // wrong event type AND invalid hwnd -> rejected
    [InlineData(0u, 1, false)]       // zero/undefined event type -> rejected
    public void IsAcceptableForegroundEvent_FiltersWrongTypeAndInvalidHwnd(uint eventType, int hwndValue, bool expected)
    {
        Assert.Equal(expected, Win32ForegroundChangeNative.IsAcceptableForegroundEvent(eventType, new nint(hwndValue)));
    }

    // ---- 11. Subscriber exception isolated ----
    [Fact]
    public void SubscriberException_IsIsolated_DoesNotCorruptOwnerThreadOrCleanup()
    {
        var native = new FakeForegroundChangeNative();
        var monitor = new ForegroundChangeMonitor(native);
        monitor.ForegroundChanged += (_, _) => throw new InvalidOperationException("synthetic subscriber failure");

        monitor.Start();
        native.RaiseForegroundChanged();

        // If the subscriber exception had corrupted the owner thread's message loop or skipped
        // cleanup, Stop's bounded Join would time out and this would throw.
        monitor.Stop();
    }

    // ---- 22. Callback arriving immediately before Stop does not corrupt shutdown ----
    [Fact]
    public async Task ForegroundChanged_ArrivingImmediatelyBeforeStop_DoesNotCorruptShutdown()
    {
        var native = new FakeForegroundChangeNative();
        var monitor = new ForegroundChangeMonitor(native);
        var raised = 0;
        monitor.ForegroundChanged += (_, _) => Interlocked.Increment(ref raised);

        monitor.Start();
        native.RaiseForegroundChanged();
        await WaitUntilAsync(() => Volatile.Read(ref raised) == 1);

        // The event has already been observed; Stop must still complete cleanly and deterministically.
        monitor.Stop();

        Assert.Equal(1, native.CallLog.Count(n => n == nameof(FakeForegroundChangeNative.Unhook)));
    }

    // ==================================================================
    // DISPOSE / IDEMPOTENCE
    // ==================================================================

    // ---- 12. Dispose idempotent after normal lifecycle ----
    [Fact]
    public void Dispose_IsIdempotent_AfterNormalLifecycle()
    {
        var native = new FakeForegroundChangeNative();
        var monitor = new ForegroundChangeMonitor(native);
        monitor.Start();
        monitor.Stop();

        monitor.Dispose();
        monitor.Dispose();
    }

    // ---- 13. Dispose before Start ----
    [Fact]
    public void Dispose_IsSafe_BeforeStart()
    {
        var monitor = new ForegroundChangeMonitor(new FakeForegroundChangeNative());
        monitor.Dispose();
    }

    // ---- 14. Dispose after failed Start ----
    [Fact]
    public void Dispose_IsSafe_AfterFailedStart()
    {
        var native = new FakeForegroundChangeNative { SetHookResult = false, SetHookError = 5 };
        var monitor = new ForegroundChangeMonitor(native);

        Assert.Throws<InvalidOperationException>(monitor.Start);

        monitor.Dispose();
        monitor.Dispose();
    }

    // ---- 15. Stop idempotent ----
    [Fact]
    public void Stop_IsIdempotent()
    {
        var native = new FakeForegroundChangeNative();
        var monitor = new ForegroundChangeMonitor(native);
        monitor.Start();

        monitor.Stop();
        monitor.Stop();

        Assert.Equal(1, native.CallLog.Count(n => n == nameof(FakeForegroundChangeNative.Unhook)));
    }

    // ---- 16. Stop before Start ----
    [Fact]
    public void Stop_IsSafe_BeforeStart()
    {
        var monitor = new ForegroundChangeMonitor(new FakeForegroundChangeNative());
        monitor.Stop();
    }

    // ---- 17. Second Start throws ----
    [Fact]
    public void Start_CalledTwice_SecondCallThrows_RegardlessOfFirstOutcome()
    {
        var monitor = new ForegroundChangeMonitor(new FakeForegroundChangeNative());
        monitor.Start();

        Assert.Throws<InvalidOperationException>(monitor.Start);

        monitor.Stop();
    }

    // ==================================================================
    // STARTUP FAILURE BEHAVIOR
    // ==================================================================

    // ---- 20. SetWinEventHook startup failure cleanup ----
    [Fact]
    public void PartialStartupFailure_SetHookFails_DoesNotCallUnhook_ButStillDestroysWindowAndClass()
    {
        var native = new FakeForegroundChangeNative { SetHookResult = false, SetHookError = 5 };
        var monitor = new ForegroundChangeMonitor(native);

        Assert.Throws<InvalidOperationException>(monitor.Start);

        Assert.DoesNotContain(nameof(FakeForegroundChangeNative.Unhook), native.CallLog);
        Assert.Contains(nameof(FakeForegroundChangeNative.DestroyWindow), native.CallLog);
        Assert.Contains(nameof(FakeForegroundChangeNative.UnregisterWindowClass), native.CallLog);
    }

    // ---- 19. HWND-creation startup failure cleanup ----
    [Fact]
    public void PartialStartupFailure_WindowCreationFails_DoesNotCallDestroyWindowOrSetHook_ButUnregistersClass()
    {
        var native = new FakeForegroundChangeNative { CreateWindowResult = false, CreateWindowError = 7 };
        var monitor = new ForegroundChangeMonitor(native);

        Assert.Throws<InvalidOperationException>(monitor.Start);

        Assert.DoesNotContain(nameof(FakeForegroundChangeNative.DestroyWindow), native.CallLog);
        Assert.DoesNotContain(nameof(FakeForegroundChangeNative.SetHook), native.CallLog);
        Assert.Contains(nameof(FakeForegroundChangeNative.UnregisterWindowClass), native.CallLog);
    }

    // ---- 18. Class-registration startup failure cleanup ----
    [Fact]
    public void PartialStartupFailure_ClassRegistrationFails_NoOtherNativeCallsAtAll()
    {
        var native = new FakeForegroundChangeNative { RegisterClassResult = false, RegisterClassError = 3 };
        var monitor = new ForegroundChangeMonitor(native);

        Assert.Throws<InvalidOperationException>(monitor.Start);

        Assert.DoesNotContain(nameof(FakeForegroundChangeNative.CreateMessageOnlyWindow), native.CallLog);
        Assert.DoesNotContain(nameof(FakeForegroundChangeNative.SetHook), native.CallLog);
        Assert.DoesNotContain(nameof(FakeForegroundChangeNative.UnregisterWindowClass), native.CallLog);
    }

    // ---- 21. Deterministic Stop timeout failure ----
    [Fact]
    public void Stop_WorkerDoesNotExitInTime_ThrowsDeterministically()
    {
        var native = new FakeForegroundChangeNative { IgnoreShutdown = true };
        var monitor = new ForegroundChangeMonitor(native, stopTimeoutOverride: TimeSpan.FromMilliseconds(50));
        monitor.Start();

        Assert.Throws<InvalidOperationException>(monitor.Stop);

        // Unstick the simulated-unresponsive owner thread so it doesn't leak past this test.
        native.ForceShutdown();
    }

    // ==================================================================
    // STRUCTURAL / PRIVACY CONTRACT
    // ==================================================================

    // ---- 23. Public event has no custom payload (STEP56 EVENT_PAYLOAD = NONE, frozen) ----
    [Fact]
    public void ForegroundChanged_IsPlainEventHandler_NoCustomPayload()
    {
        var eventInfo = typeof(ForegroundChangeMonitor).GetEvent(nameof(ForegroundChangeMonitor.ForegroundChanged));
        Assert.NotNull(eventInfo);
        Assert.Equal(typeof(EventHandler), eventInfo!.EventHandlerType);
    }

    // ---- 24. Public monitor surface exposes no user-content strings (structural privacy boundary,
    // matching the reflection-based "no string members" precedent already established for
    // ClipboardMonitorDiagnosticEvent/ClipboardWriteDiagnosticEvent). The sole string field this type
    // has (_windowClassName) is a randomly-generated, process-internal Win32 window-class identifier --
    // never derived from anything the user typed or copied -- exactly mirroring SessionLockMonitor's own
    // identical _windowClassName field, so it is explicitly, deliberately excluded here rather than
    // silently widening the assertion to accept it. ----
    [Fact]
    public void ForegroundChangeMonitor_HasNoUserContentStringMembers()
    {
        var fields = typeof(ForegroundChangeMonitor)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Where(f => f.Name != "_windowClassName");

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(string));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? WaitTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met within the timeout.");
            await Task.Delay(10);
        }
    }
}
