using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Phase 3C STEP38 -- the one test in this project that touches real Win32/WTS APIs
// (Win32SessionLockNative, not the fake). Verifies only that a real message-only window can be
// created and registered for THIS session's WTS notifications, and cleanly torn down -- it never
// waits for, simulates, or requires an actual session lock/unlock event. Kept in the same
// WindowsSmokeCollection as the clipboard/composer real-resource smoke tests so real-OS-resource
// tests never run concurrently with each other (Phase 3A.4 STEP1 report's TEST_STRATEGY finding).
// MANUAL_WINDOWS_SESSION_LOCK_SMOKE (real Win+L delivery) remains a separate, OPEN manual QA item --
// this automated test does not close it.
[Collection(nameof(WindowsSmokeCollection))]
public class SessionLockMonitorWindowsSmokeTests
{
    [Fact]
    public void RealWin32_StartThenStop_SucceedsWithoutRequiringAnActualLockEvent()
    {
        var monitor = new SessionLockMonitor();

        monitor.Start();
        monitor.Stop();
    }
}
