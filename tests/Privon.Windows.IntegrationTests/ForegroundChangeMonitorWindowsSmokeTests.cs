using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Phase 0.2B STEP57 -- the tests in this project that touch real Win32 APIs (Win32ForegroundChangeNative,
// not the fake). Verifies that a real message-only window + real SetWinEventHook registration can be
// created and cleanly torn down, and that repeated Start/Stop cycles do not leak native resources. Never
// inspects, logs, or asserts anything about which REAL user window/process is foreground.
//
// DEFERRED_TO_MANUAL_QA (STEP57 instruction's own explicit escape hatch: "If a deterministic controlled
// foreground transition cannot be produced safely in automation: do not weaken the test or add keyboard
// automation. Mark actual delivery as MANUAL QA and retain real Start/Stop smoke automatically."):
// producing a genuinely reliable, deterministic real EVENT_SYSTEM_FOREGROUND transition from this test
// process would require SetForegroundWindow on a throwaway window this process itself creates --
// SetForegroundWindow is well-documented as unreliable when called by a background/non-foreground
// process (Windows' foreground-lock-timeout heuristics silently degrade it to a taskbar-flash instead of
// an actual focus change unless the calling process already meets specific input-history conditions),
// and that unreliability is materially worse still under a non-interactive/CI test-runner session. Rather
// than add a test that could flake for reasons entirely unrelated to this component's own correctness,
// actual real-world WinEvent delivery is verified only by MANUAL_WINDOWS_FOREGROUND_CHANGE_SMOKE (a
// manual QA item, not yet performed) -- exactly the same deferral shape already established for
// MANUAL_WINDOWS_SESSION_LOCK_SMOKE/MANUAL_CHATGPT_TARGET_SMOKE elsewhere in this codebase. Kept in the
// same WindowsSmokeCollection as the clipboard/composer/session-lock real-resource smoke tests so
// real-OS-resource tests never run concurrently with each other (Phase 3A.4 STEP1 report's TEST_STRATEGY
// finding).
[Collection(nameof(WindowsSmokeCollection))]
public class ForegroundChangeMonitorWindowsSmokeTests
{
    // ---- A. real SetWinEventHook installation succeeds ----
    [Fact]
    public void RealWin32_StartThenStop_SucceedsWithoutRequiringAnActualForegroundChange()
    {
        var monitor = new ForegroundChangeMonitor();

        monitor.Start();
        monitor.Stop();
    }

    // ---- B/C. Stop/Dispose removes the hook cleanly; multiple new instances can Start/Stop without
    // leaking (repeated cycles across separate instances, mirroring
    // SessionLockMonitorWindowsSmokeTests' own single-cycle precedent extended per this STEP's explicit
    // "repeated start/stop cycles do not leak" requirement) ----
    [Fact]
    public void RealWin32_RepeatedStartStopCycles_AcrossSeparateInstances_DoNotLeak()
    {
        for (int i = 0; i < 5; i++)
        {
            using var monitor = new ForegroundChangeMonitor();
            monitor.Start();
            monitor.Stop();
        }
    }

    [Fact]
    public void RealWin32_Dispose_RemovesHookCleanly()
    {
        var monitor = new ForegroundChangeMonitor();
        monitor.Start();
        monitor.Dispose();
    }

    // ---- D (DEFERRED_TO_MANUAL_QA): see this file's own class doc -- actual real WinEvent delivery
    // for a genuine foreground transition is a manual QA item, not an automated test here.

    // ---- 25. Delegate remains rooted for the hook's entire lifetime -- verified empirically against
    // the REAL native layer (the fake never allocates a real GCHandle-rooted delegate at all, so this
    // property is only meaningfully testable here): forcing a full blocking GC collection while the
    // hook is installed must never silently invalidate it. If the WinEventProc delegate were not
    // correctly rooted, a collection here could leave the hook calling into a collected/finalized
    // delegate, and this monitor's own subsequent Stop() (which itself depends on the owner thread's
    // message loop still functioning) would then time out and throw. ----
    [Fact]
    public void RealWin32_ForcedGarbageCollectionWhileHookInstalled_DoesNotInvalidateHook()
    {
        var monitor = new ForegroundChangeMonitor();
        monitor.Start();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        // If the WinEventProc delegate had been collected/invalidated, the owner thread's own
        // message loop would already be in an unknown state -- Stop's bounded Join would time out
        // and throw instead of completing cleanly.
        monitor.Stop();
    }
}
