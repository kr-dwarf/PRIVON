using System.Runtime.InteropServices;
using System.Windows.Interop;
using Privon.App;

namespace Privon.App.Tests;

// Phase 0.2G release-blocker fix -- LEVEL3_PROTECT_SELF_STALE_ROOT_CAUSE regression.
//
// ROOT CAUSE (confirmed via real, live cross-app manual observation against a real ChatGPT
// Desktop window and a real running Privon.App.exe, not simulated): DecisionPromptWindow is shown
// via a plain WPF Window.Show() call from a background/tray process reacting to a system event
// (never a user-initiated "open this window" action). Windows' anti-focus-stealing protection
// reliably prevented Show()-time activation from stealing OS foreground away from the real target
// app (ChatGPT stayed the real foreground window the whole time the popup was visible, confirmed
// by live GetForegroundWindow() observation) -- but that protection does NOT extend to a genuine,
// real user mouse click on the popup's own button: a real click always activates the window under
// the cursor at the OS level (WM_MOUSEACTIVATE), and this happens BEFORE the click even reaches
// WPF's managed Button.Click handler. So at the exact moment
// ClipboardDecisionActionResolver.ResolveAsync's fresh IForegroundTargetCapture.Capture() call
// ran, the real foreground window was this popup itself (Privon.App), not ChatGPT --
// TargetGate.IsSupportedTarget correctly (and safely) rejected it, and ResolveAsync correctly
// returned Stale. The fix is NOT to weaken TargetGate/the resolver's validation in any way; it is
// to stop this popup from EVER being able to take real OS activation in the first place -- at
// Show() time (already effectively true via the OS heuristic above, but not guaranteed) AND, more
// importantly, at click time (which the OS heuristic above does NOT cover) -- via the WS_EX_NOACTIVATE
// extended window style. A real user click still lands on and fires the button's Click event
// normally under WS_EX_NOACTIVATE (only window ACTIVATION is suppressed, not mouse-message
// delivery) -- see DecisionPromptWindow's own doc for the exact mechanism.
//
// IMPORTANT (explicit instruction from the Phase 0.2G session): do not assume ShowActivated=false
// alone is sufficient. This file's own test below (WITHOUT the WS_EX_NOACTIVATE fix) would show
// ShowActivated is already false-by-default-intent but the underlying HWND's extended style would
// still lack WS_EX_NOACTIVATE -- confirming ShowActivated is a WPF-level SHOW-time hint only, not
// an OS-level guarantee against click-time activation. This test asserts the REAL Win32 extended
// window style bit on a REAL HWND (via a genuine, pumped WPF Dispatcher thread -- DispatcherAffineTestHost,
// the same real-dispatcher infrastructure ProtectAllDispatcherAffinityTests.cs already
// established), not an approximation.
public class DecisionPromptWindowActivationTests
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [Fact]
    public void DecisionPromptWindow_ShowActivatedIsFalse()
    {
        using var host = new DispatcherAffineTestHost();

        bool showActivated = host.Dispatcher.Invoke(() =>
        {
            var window = new DecisionPromptWindow();
            try
            {
                return window.ShowActivated;
            }
            finally
            {
                window.Close();
            }
        });

        Assert.False(showActivated);
    }

    // The actual root-cause regression: proves the REAL underlying HWND -- after Show(), the exact
    // point at which a real user click could occur -- structurally can never take OS activation,
    // regardless of any OS-level heuristic that may or may not apply on a given machine/session.
    [Fact]
    public void DecisionPromptWindow_AfterShow_RealHwndHasWsExNoActivateStyle()
    {
        using var host = new DispatcherAffineTestHost();

        int exStyle = host.Dispatcher.Invoke(() =>
        {
            var window = new DecisionPromptWindow();
            try
            {
                window.Show();
                var hwnd = new WindowInteropHelper(window).Handle;
                Assert.NotEqual(IntPtr.Zero, hwnd);
                return GetWindowLong(hwnd, GWL_EXSTYLE);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(
            (exStyle & WS_EX_NOACTIVATE) == WS_EX_NOACTIVATE,
            $"Real HWND extended style (0x{exStyle:X8}) does not include WS_EX_NOACTIVATE (0x{WS_EX_NOACTIVATE:X8}) -- " +
            "this popup can still steal real OS foreground activation from the authorized target app on a real user click.");
    }
}
