using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Phase 3C STEP38 -- Session Lock Monitor regression. All tests here use FakeSessionLockNative
// (synthetic, OS-free) -- no real Windows window/WTS session is touched by anything in this file,
// and no automated test ever locks the actual developer workstation. See
// SessionLockMonitorWindowsSmokeTests.cs for the one real-Win32 smoke test (registration/cleanup
// mechanics only -- it never waits for or simulates an actual lock event).
public class SessionLockMonitorTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    // ==================================================================
    // REGISTRATION LIFECYCLE / CLEANUP ORDERING
    // ==================================================================

    // ---- 1. Start registers the session notification exactly once; cleanup order matches the
    // required reverse-of-startup sequence ----
    [Fact]
    public void Start_RegistersSessionNotificationOnce_AndOrdersCleanupCorrectly()
    {
        var native = new FakeSessionLockNative();
        var monitor = new SessionLockMonitor(native);

        monitor.Start();
        Assert.Equal(1, native.CallLog.Count(n => n == nameof(FakeSessionLockNative.RegisterSessionNotification)));

        monitor.Stop();

        Assert.Equal(
            [
                nameof(FakeSessionLockNative.RegisterWindowClass),
                nameof(FakeSessionLockNative.CreateMessageOnlyWindow),
                nameof(FakeSessionLockNative.RegisterSessionNotification),
                nameof(FakeSessionLockNative.UnregisterSessionNotification),
                nameof(FakeSessionLockNative.DestroyWindow),
                nameof(FakeSessionLockNative.UnregisterWindowClass),
            ],
            native.CallLog);
    }

    [Fact]
    public void Stop_UnregistersSessionNotificationOnce()
    {
        var native = new FakeSessionLockNative();
        var monitor = new SessionLockMonitor(native);

        monitor.Start();
        monitor.Stop();

        Assert.Equal(1, native.CallLog.Count(n => n == nameof(FakeSessionLockNative.UnregisterSessionNotification)));
    }

    [Fact]
    public void StartThenStop_CompletesWithoutException()
    {
        var native = new FakeSessionLockNative();
        var monitor = new SessionLockMonitor(native);

        monitor.Start();
        monitor.Stop();
    }

    // ==================================================================
    // LOCK TRANSLATION / EVENT FILTERING
    // ==================================================================

    [Fact]
    public async Task Locked_RaisedOnLockMessage()
    {
        var native = new FakeSessionLockNative();
        var monitor = new SessionLockMonitor(native);
        var raised = 0;
        monitor.Locked += (_, _) => Interlocked.Increment(ref raised);

        monitor.Start();
        native.RaiseLocked();

        await WaitUntilAsync(() => Volatile.Read(ref raised) == 1);

        monitor.Stop();
    }

    [Fact]
    public async Task Locked_NotRaised_ForOtherWtsReasonCode()
    {
        var native = new FakeSessionLockNative();
        var monitor = new SessionLockMonitor(native);
        var raised = 0;
        monitor.Locked += (_, _) => Interlocked.Increment(ref raised);

        monitor.Start();
        native.RaiseOther(); // e.g. WTS_SESSION_UNLOCK, logon/logoff, connect/disconnect
        // Follow with a real Locked so we deterministically know the loop already processed the
        // "Other" message (no sleep-based race) before asserting it never fired Locked.
        native.RaiseLocked();
        await WaitUntilAsync(() => Volatile.Read(ref raised) == 1);

        Assert.Equal(1, raised); // exactly the second (Locked) raise -- the first (Other) never counted

        monitor.Stop();
    }

    [Fact]
    public async Task Locked_RaisedOnceMorePerRepeatedLockMessage_NoDedupOrDebounce()
    {
        var native = new FakeSessionLockNative();
        var monitor = new SessionLockMonitor(native);
        var raised = 0;
        monitor.Locked += (_, _) => Interlocked.Increment(ref raised);

        monitor.Start();
        native.RaiseLocked();
        native.RaiseLocked();
        native.RaiseLocked();

        await WaitUntilAsync(() => Volatile.Read(ref raised) == 3);

        monitor.Stop();
    }

    [Fact]
    public void Locked_NeverRaisedAfterStop()
    {
        var native = new FakeSessionLockNative();
        var monitor = new SessionLockMonitor(native);
        var raised = 0;
        monitor.Locked += (_, _) => Interlocked.Increment(ref raised);

        monitor.Start();
        monitor.Stop();

        // The owner thread has already confirmed exit (Stop's bounded Join returned successfully) --
        // no further message, including one queued but never drained, can reach RaiseLocked.
        Assert.Equal(0, raised);
    }

    [Fact]
    public void SubscriberException_IsIsolated_DoesNotCorruptOwnerThreadOrCleanup()
    {
        var native = new FakeSessionLockNative();
        var monitor = new SessionLockMonitor(native);
        monitor.Locked += (_, _) => throw new InvalidOperationException("synthetic subscriber failure");

        monitor.Start();
        native.RaiseLocked();

        // If the subscriber exception had corrupted the owner thread's message loop or skipped
        // cleanup, Stop's bounded Join would time out and this would throw.
        monitor.Stop();
    }

    // ==================================================================
    // DISPOSE / IDEMPOTENCE
    // ==================================================================

    [Fact]
    public void Dispose_IsIdempotent_AfterNormalLifecycle()
    {
        var native = new FakeSessionLockNative();
        var monitor = new SessionLockMonitor(native);
        monitor.Start();
        monitor.Stop();

        monitor.Dispose();
        monitor.Dispose();
    }

    [Fact]
    public void Dispose_IsSafe_BeforeStart()
    {
        var monitor = new SessionLockMonitor(new FakeSessionLockNative());
        monitor.Dispose();
    }

    [Fact]
    public void Dispose_IsSafe_AfterFailedStart()
    {
        var native = new FakeSessionLockNative { RegisterSessionNotificationResult = false, RegisterSessionNotificationError = 5 };
        var monitor = new SessionLockMonitor(native);

        Assert.Throws<InvalidOperationException>(monitor.Start);

        monitor.Dispose();
        monitor.Dispose();
    }

    [Fact]
    public void Stop_IsIdempotent()
    {
        var native = new FakeSessionLockNative();
        var monitor = new SessionLockMonitor(native);
        monitor.Start();

        monitor.Stop();
        monitor.Stop();

        Assert.Equal(1, native.CallLog.Count(n => n == nameof(FakeSessionLockNative.UnregisterSessionNotification)));
    }

    [Fact]
    public void Stop_IsSafe_BeforeStart()
    {
        var monitor = new SessionLockMonitor(new FakeSessionLockNative());
        monitor.Stop();
    }

    [Fact]
    public void Start_CalledTwice_SecondCallThrows_RegardlessOfFirstOutcome()
    {
        var monitor = new SessionLockMonitor(new FakeSessionLockNative());
        monitor.Start();

        Assert.Throws<InvalidOperationException>(monitor.Start);

        monitor.Stop();
    }

    // ==================================================================
    // REGISTRATION FAILURE BEHAVIOR
    // ==================================================================

    [Fact]
    public void PartialStartupFailure_RegisterSessionNotificationFails_DoesNotCallUnregister_ButStillDestroysWindowAndClass()
    {
        var native = new FakeSessionLockNative { RegisterSessionNotificationResult = false, RegisterSessionNotificationError = 5 };
        var monitor = new SessionLockMonitor(native);

        Assert.Throws<InvalidOperationException>(monitor.Start);

        Assert.DoesNotContain(nameof(FakeSessionLockNative.UnregisterSessionNotification), native.CallLog);
        Assert.Contains(nameof(FakeSessionLockNative.DestroyWindow), native.CallLog);
        Assert.Contains(nameof(FakeSessionLockNative.UnregisterWindowClass), native.CallLog);
    }

    [Fact]
    public void PartialStartupFailure_WindowCreationFails_DoesNotCallDestroyWindowOrRegisterNotification_ButUnregistersClass()
    {
        var native = new FakeSessionLockNative { CreateWindowResult = false, CreateWindowError = 7 };
        var monitor = new SessionLockMonitor(native);

        Assert.Throws<InvalidOperationException>(monitor.Start);

        Assert.DoesNotContain(nameof(FakeSessionLockNative.DestroyWindow), native.CallLog);
        Assert.DoesNotContain(nameof(FakeSessionLockNative.RegisterSessionNotification), native.CallLog);
        Assert.Contains(nameof(FakeSessionLockNative.UnregisterWindowClass), native.CallLog);
    }

    [Fact]
    public void PartialStartupFailure_ClassRegistrationFails_NoOtherNativeCallsAtAll()
    {
        var native = new FakeSessionLockNative { RegisterClassResult = false, RegisterClassError = 3 };
        var monitor = new SessionLockMonitor(native);

        Assert.Throws<InvalidOperationException>(monitor.Start);

        Assert.DoesNotContain(nameof(FakeSessionLockNative.CreateMessageOnlyWindow), native.CallLog);
        Assert.DoesNotContain(nameof(FakeSessionLockNative.RegisterSessionNotification), native.CallLog);
        Assert.DoesNotContain(nameof(FakeSessionLockNative.UnregisterWindowClass), native.CallLog);
    }

    [Fact]
    public void Stop_WorkerDoesNotExitInTime_ThrowsDeterministically()
    {
        var native = new FakeSessionLockNative { IgnoreShutdown = true };
        var monitor = new SessionLockMonitor(native, stopTimeoutOverride: TimeSpan.FromMilliseconds(50));
        monitor.Start();

        Assert.Throws<InvalidOperationException>(monitor.Stop);

        // Unstick the simulated-unresponsive owner thread so it doesn't leak past this test.
        native.ForceShutdown();
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
