using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Phase 3A.4 STEP2 -- the one test in this project that touches real Win32 APIs
// (Win32ClipboardMonitorNative, not the fake). Verifies only that a real message-only window
// can be created and registered as a clipboard format listener, and cleanly torn down -- it
// never calls OpenClipboard/GetClipboardData/SetClipboardData (not implemented yet) and never
// reads or mutates the actual system clipboard content. Kept in its own collection so it never
// runs concurrently with another real-listener test, per the Phase 3A.4 STEP1 report's
// TEST_STRATEGY finding (global clipboard/listener resources are not parallel-safe).
[Collection(nameof(WindowsSmokeCollection))]
public class ClipboardChangeMonitorWindowsSmokeTests
{
    [Fact]
    public void RealWin32_StartThenStop_SucceedsWithoutTouchingClipboardContent()
    {
        var monitor = new ClipboardChangeMonitor();

        monitor.Start();
        monitor.Stop();
    }
}

[CollectionDefinition(nameof(WindowsSmokeCollection))]
public class WindowsSmokeCollection;
