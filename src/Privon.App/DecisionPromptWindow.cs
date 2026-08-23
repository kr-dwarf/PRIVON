using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Privon.App;

/// <summary>
/// Phase 3C STEP40 -- the only production implementation of <see cref="IDecisionPromptSurface"/>.
/// Deliberately minimal: no dashboard, no settings, no clipboard/raw-text preview, no
/// <c>CanonicalValue</c> display, no PiiType display -- a single generic Korean message plus a
/// single "모두 보호" button (Phase 3C STEP39/STEP40's frozen MINIMUM_DECISION_ACTION). Built
/// entirely in code (no separate .xaml) to keep this one small, self-contained file the whole
/// surface. Must only ever be constructed on a thread with a live WPF <see cref="Dispatcher"/> --
/// i.e. only from <see cref="DecisionPromptCoordinator"/>'s dispatcher-marshaled callback, never
/// directly from a background thread.
///
/// <see cref="IDecisionPromptSurface.Closed"/> is implemented explicitly, forwarding to this
/// type's OWN base <see cref="Window.Closed"/> event -- so it fires identically whether this
/// window is closed programmatically (<see cref="Close"/>, e.g. because a newer scope superseded
/// it) or by the user's own system close button, without this type needing to distinguish the two
/// (Phase 3C STEP40 instruction's USER_CLOSING_THE_PROMPT: closing never resolves anything).
///
/// NON_ACTIVATING_PROMPT (Phase 0.2G release-blocker fix -- LEVEL3_PROTECT_SELF_STALE_ROOT_CAUSE,
/// confirmed via live cross-app manual observation against a real ChatGPT Desktop window and a
/// real running Privon.App.exe, not simulated): this window MUST NEVER be able to take real OS
/// foreground/activation, at <see cref="Show"/> time OR when the user clicks its own button --
/// because <see cref="ClipboardDecisionActionResolver.ResolveAsync"/>'s very first substantive
/// step is a FRESH <see cref="IForegroundTargetCapture.Capture"/> + <see cref="TargetGate.IsSupportedTarget"/>
/// check (by design -- see that method's own FRESH_TARGET doc, never weakened by this fix), and if
/// THIS window is itself the real foreground process at that exact moment, <c>TargetGate</c>
/// correctly (and safely) rejects it as an unsupported target, and the whole Protect-All attempt
/// is reported <c>Stale</c> -- with the raw PII left completely unprotected on the clipboard. The
/// fix here is NOT to weaken TargetGate/the resolver's validation in any way (that safety contract
/// stays exactly as strict as it always was) -- it is to stop this popup from ever being able to
/// pollute the fresh target capture with itself in the first place, so that capture continues to
/// see whatever the REAL currently-authorized foreground app actually is (ChatGPT, when the user
/// legitimately stayed there; something else entirely, correctly rejected, if the user actually
/// navigated away -- this fix changes neither outcome).
///
/// TWO SEPARATE OS BEHAVIORS, NOT ONE (do not assume <see cref="Window.ShowActivated"/> = false
/// alone is sufficient -- confirmed empirically, not assumed, during the Phase 0.2G session):
///   1. SHOW-TIME activation: live observation (real <c>GetForegroundWindow()</c> polling against
///      a real running instance) showed Windows' own anti-focus-stealing heuristic already
///      reliably prevented a plain <see cref="Show"/> call from a background/tray process (this
///      one, reacting to a system event, never a user-initiated "open a window" action) from
///      stealing OS foreground away from the real target app -- ChatGPT stayed the genuine
///      foreground window the entire time this popup was visible-but-unclicked. Setting
///      <see cref="ShowActivated"/> to <see langword="false"/> here makes that observed behavior an
///      explicit, guaranteed WPF-level intent instead of an incidental OS heuristic outcome.
///   2. CLICK-TIME activation: that SAME anti-focus-stealing heuristic does NOT cover a genuine
///      user mouse click on this window's own button -- a real click always activates the window
///      under the cursor at the OS level (<c>WM_MOUSEACTIVATE</c>), and this happens BEFORE the
///      click message even reaches WPF's managed <c>Button.Click</c> handler. <see cref="ShowActivated"/>
///      has no effect on this at all -- it is a one-time SHOW-time hint, not a standing
///      no-activate guarantee. Only the Win32 extended window style <c>WS_EX_NOACTIVATE</c>
///      (applied below, via <see cref="OnSourceInitialized"/>, once this window's real HWND
///      exists) structurally suppresses activation on EVERY subsequent input event, including a
///      real click -- while leaving ordinary mouse-message delivery (and therefore the button's
///      own <c>Click</c> event) completely unaffected; <c>WS_EX_NOACTIVATE</c> only changes how
///      the OS responds to <c>WM_MOUSEACTIVATE</c> (<c>MA_NOACTIVATE</c> instead of the default
///      <c>MA_ACTIVATE</c>), never whether mouse-button messages are delivered at all. <see cref="Topmost"/>
///      stays <see langword="true"/> and is unaffected -- Z-order visibility and activation are
///      independent Win32 concepts; this window remains visible on top of ChatGPT throughout,
///      exactly as before, it simply never becomes the "active"/foreground window while doing so.
/// </summary>
internal sealed class DecisionPromptWindow : Window, IDecisionPromptSurface
{
    // NATIVE_INTEROP_BOUNDARY: narrow, capability-scoped P/Invoke, exactly like every other native
    // seam in this codebase (e.g. Win32ForegroundTargetSource.NativeMethods) -- never a shared/god
    // NativeMethods file. GWL_EXSTYLE is documented as a 32-bit DWORD value on both 32-bit and
    // 64-bit Windows (unlike pointer-sized fields such as GWLP_WNDPROC), so the plain 32-bit
    // GetWindowLong/SetWindowLong pair is correct here without needing the *Ptr variants.
    private static class NativeMethods
    {
        internal const int GWL_EXSTYLE = -20;
        internal const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }

    private readonly TextBlock _messageText;
    private readonly System.Windows.Controls.Button _protectAllButton;

    public DecisionPromptWindow()
    {
        Title = "PRIVON";
        Width = 380;
        Height = 160;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowActivated = false;

        _messageText = new TextBlock
        {
            Text = "개인정보가 감지되었습니다.\n클립보드 내용을 보호하려면 보호하기를 눌러주세요.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 16, 16, 8),
        };

        _protectAllButton = new System.Windows.Controls.Button
        {
            Content = "모두 보호",
            Margin = new Thickness(16, 0, 16, 16),
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        _protectAllButton.Click += (_, _) => ProtectAllRequested?.Invoke(this, EventArgs.Empty);

        var panel = new StackPanel();
        panel.Children.Add(_messageText);
        panel.Children.Add(_protectAllButton);
        Content = panel;

        SourceInitialized += OnSourceInitialized;
    }

    // Fires once this window's real HWND has been created (before Show() actually paints it) --
    // the earliest point WindowInteropHelper.Handle is valid. Sets WS_EX_NOACTIVATE unconditionally
    // -- see this type's own NON_ACTIVATING_PROMPT doc for why ShowActivated alone (a SHOW-time-only
    // hint) cannot cover click-time activation, which is what this extended style actually fixes.
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_NOACTIVATE);
    }

    public event EventHandler? ProtectAllRequested;

    event EventHandler? IDecisionPromptSurface.Closed
    {
        add => Closed += value;
        remove => Closed -= value;
    }

    public void DisableProtectAction() => _protectAllButton.IsEnabled = false;

    public void ShowNeutralFailure(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _messageText.Text = message;
        _protectAllButton.IsEnabled = false;
    }

    void IDecisionPromptSurface.Show() => Show();

    void IDecisionPromptSurface.Close() => Close();
}
