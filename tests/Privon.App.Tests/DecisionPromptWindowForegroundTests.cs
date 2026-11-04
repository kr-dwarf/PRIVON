using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Privon.App;

namespace Privon.App.Tests;

// The real DecisionPromptWindow's evidence about how it was pressed, on a real WPF dispatcher. Nothing here sends input to the OS or changes the user's
// foreground window: key events are routed straight to the window's own button, a UIA Invoke goes through the button's own automation peer, and the
// WM_ACTIVATEAPP thread-id handling is driven directly. The window is non-activating, so it is never the real foreground here.
public class DecisionPromptWindowForegroundTests
{
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [Fact]
    public void BeforeTheWindowHasAHandle_NothingIsReported_AndItDoesNotThrow()
    {
        using var host = new DispatcherAffineTestHost();

        DecisionPromptInvocation seen = host.Dispatcher.Invoke(() =>
        {
            var window = new DecisionPromptWindow();
            try { return ((IDecisionPromptSurface)window).CaptureInvocation(); }
            finally { window.Close(); }
        });

        Assert.Equal(default, seen);
    }

    [Fact]
    public void ANonActivatingWindowThatIsShown_IsNotReportedAsTheForeground()
    {
        using var host = new DispatcherAffineTestHost();

        DecisionPromptInvocation seen = host.Dispatcher.Invoke(() =>
        {
            var window = new DecisionPromptWindow();
            try
            {
                window.Show();
                Assert.NotEqual(IntPtr.Zero, new WindowInteropHelper(window).Handle);
                return ((IDecisionPromptSurface)window).CaptureInvocation();
            }
            finally { window.Close(); }
        });

        Assert.False(seen.IsForeground);   // a mouse click on this window leaves the supported app in the foreground: that is exactly what false means here
    }

    // ===== the key is bound to the click it raises, and to nothing else =====================================================================

    private static Button ProtectButton(DecisionPromptWindow window) =>
        ((Panel)window.Content).Children.OfType<Button>().Single();

    private static TextBlock Message(DecisionPromptWindow window) =>
        ((Panel)window.Content).Children.OfType<TextBlock>().Single();

    private static KeyEventArgs KeyArgs(DecisionPromptWindow window, RoutedEvent routed, Key key) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key) { RoutedEvent = routed };

    /// <summary>Routes one key to <paramref name="target"/> the way the input system does: the tunnelling event, then the bubbling event.</summary>
    private static void RouteKey(DecisionPromptWindow window, UIElement target, Key key, bool up = false)
    {
        target.RaiseEvent(KeyArgs(window, up ? Keyboard.PreviewKeyUpEvent : Keyboard.PreviewKeyDownEvent, key));
        target.RaiseEvent(KeyArgs(window, up ? Keyboard.KeyUpEvent : Keyboard.KeyDownEvent, key));
    }

    private static List<DecisionPromptInvocation> Presses(DecisionPromptWindow window)
    {
        var seen = new List<DecisionPromptInvocation>();
        ((IDecisionPromptSurface)window).ProtectAllRequested += (_, _) => seen.Add(((IDecisionPromptSurface)window).CaptureInvocation());
        return seen;
    }

    private static void Flush(DispatcherAffineTestHost host) => host.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

    [Fact]
    public void AFreshEnterAddressedToTheButton_IsReportedOnlyForTheClickItRaises()
    {
        using var host = new DispatcherAffineTestHost();

        var (presses, after) = host.Dispatcher.Invoke(() =>
        {
            var window = new DecisionPromptWindow();
            try
            {
                window.Show();
                var seen = Presses(window);
                RouteKey(window, ProtectButton(window), Key.Return);
                return (seen, ((IDecisionPromptSurface)window).CaptureInvocation());
            }
            finally { window.Close(); }
        });

        Assert.Single(presses);
        Assert.True(presses[0].ByKeyboard);          // the click was raised inside that Enter's routing
        Assert.False(after.ByKeyboard);               // and the flag does not outlive it
    }

    [Fact]
    public void AFreshSpaceAddressedToTheButton_IsReportedOnlyForTheClickItRaises()
    {
        using var host = new DispatcherAffineTestHost();

        var (presses, after) = host.Dispatcher.Invoke(() =>
        {
            var window = new DecisionPromptWindow();
            try
            {
                window.Show();
                var seen = Presses(window);
                var button = ProtectButton(window);
                button.RaiseEvent(KeyArgs(window, Keyboard.PreviewKeyDownEvent, Key.Space));
                button.RaiseEvent(KeyArgs(window, Keyboard.KeyDownEvent, Key.Space));      // presses the button; the click comes on the key UP
                Assert.Empty(seen);
                RouteKey(window, button, Key.Space, up: true);
                return (seen, ((IDecisionPromptSurface)window).CaptureInvocation());
            }
            finally { window.Close(); }
        });

        Assert.Single(presses);
        Assert.True(presses[0].ByKeyboard);
        Assert.False(after.ByKeyboard);
    }

    [Fact]
    public void AKeyAddressedToAnythingElse_NeverBindsToALaterClick()
    {
        using var host = new DispatcherAffineTestHost();

        var presses = host.Dispatcher.Invoke(() =>
        {
            var window = new DecisionPromptWindow();
            try
            {
                window.Show();
                var seen = Presses(window);
                RouteKey(window, Message(window), Key.Return);       // an Enter that landed on the message text, or on the window, not the button
                RouteKey(window, window, Key.Return);
                RouteKey(window, window, Key.Space, up: true);
                ProtectButton(window).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // then a programmatic click
                return seen;
            }
            finally { window.Close(); }
        });

        Assert.Single(presses);
        Assert.False(presses[0].ByKeyboard);
    }

    [Fact]
    public void ASpaceKeyUpWithoutAFreshKeyDownOnTheButton_IsNotReportedAsKeyboard()
    {
        using var host = new DispatcherAffineTestHost();

        var presses = host.Dispatcher.Invoke(() =>
        {
            var window = new DecisionPromptWindow();
            try
            {
                window.Show();
                var seen = Presses(window);
                var button = ProtectButton(window);
                // WPF's button is pressed by every Space key down, auto-repeats of a Space held before the focus arrived included; here a click is raised inside the key UP
                button.PreviewKeyUp += (_, _) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                RouteKey(window, button, Key.Space, up: true);                        // a key up whose key down this button never saw as a fresh press

                button.RaiseEvent(KeyArgs(window, Keyboard.PreviewKeyDownEvent, Key.Space));    // a fresh Space down ...
                button.RaiseEvent(KeyArgs(window, Keyboard.KeyDownEvent, Key.Space));
                RouteKey(window, button, Key.Tab);                                    // ... forgotten by any other key ...
                RouteKey(window, button, Key.Space, up: true);                        // ... so this key up is not a press either

                button.RaiseEvent(KeyArgs(window, Keyboard.PreviewKeyDownEvent, Key.Space));    // a fresh Space down ...
                button.RaiseEvent(KeyArgs(window, Keyboard.KeyDownEvent, Key.Space));
                window.NoteApplicationActivation(activated: true, otherAppThreadId: 0);         // ... followed by a new activation ...
                RouteKey(window, button, Key.Space, up: true);                        // ... is not carried into it
                return seen;
            }
            finally { window.Close(); }
        });

        Assert.True(presses.Count >= 3);                                   // (the third step also makes WPF's own pressed button click on that key up)
        Assert.All(presses, p => Assert.False(p.ByKeyboard));
    }

    [Fact]
    public void AClickRaisedInsideAKeyRoutedToSomethingElse_IsNotReportedAsKeyboard()
    {
        using var host = new DispatcherAffineTestHost();

        var presses = host.Dispatcher.Invoke(() =>
        {
            var window = new DecisionPromptWindow();
            try
            {
                window.Show();
                var seen = Presses(window);
                var message = Message(window);
                // some other handler, on the message text, raises a click while that Enter is being routed -- the key was not addressed to the button
                message.PreviewKeyDown += (_, _) => ProtectButton(window).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                RouteKey(window, message, Key.Return);
                return seen;
            }
            finally { window.Close(); }
        });

        Assert.Single(presses);
        Assert.False(presses[0].ByKeyboard);
    }

    [Fact]
    public void AKeyRoutingCutShort_CannotBeInheritedByALaterUiaClick()
    {
        using var host = new DispatcherAffineTestHost();
        DecisionPromptWindow? window = null;
        List<DecisionPromptInvocation>? presses = null;

        host.Dispatcher.Invoke(() =>
        {
            window = new DecisionPromptWindow();
            window.Show();
            presses = Presses(window);
            // the preview event is raised but the key event never is (a handler marked the preview handled): nothing in the routing clears the flag itself
            ProtectButton(window).RaiseEvent(KeyArgs(window, Keyboard.PreviewKeyDownEvent, Key.Return));
            Assert.True(((IDecisionPromptSurface)window).CaptureInvocation().ByKeyboard);   // still inside that key's routing
        });
        host.Dispatcher.Invoke(() => { }, DispatcherPriority.Send);   // the queued clear runs before anything else
        host.Dispatcher.Invoke(() =>
        {
            Assert.False(((IDecisionPromptSurface)window!).CaptureInvocation().ByKeyboard);
            ProtectButton(window!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window!.Close();
        });

        Assert.Single(presses!);
        Assert.False(presses![0].ByKeyboard);
    }

    [Fact]
    public void AUiaInvokeOnTheButton_IsNeverReportedAsKeyboard()
    {
        using var host = new DispatcherAffineTestHost();
        DecisionPromptWindow? window = null;
        List<DecisionPromptInvocation>? presses = null;

        host.Dispatcher.Invoke(() =>
        {
            window = new DecisionPromptWindow();
            window.Show();
            presses = Presses(window);
            var peer = UIElementAutomationPeer.CreatePeerForElement(ProtectButton(window));
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();         // what a UIA client's Invoke does: the click is queued, not routed through a key
        });
        Flush(host);
        host.Dispatcher.Invoke(() => window!.Close());

        Assert.Single(presses!);
        Assert.False(presses![0].ByKeyboard);
    }

    [Fact]
    public void ADisabledButton_NeverStartsAKeyboardRouting()
    {
        using var host = new DispatcherAffineTestHost();

        var (presses, flagged) = host.Dispatcher.Invoke(() =>
        {
            var window = new DecisionPromptWindow();
            try
            {
                window.Show();
                var seen = Presses(window);
                ((IDecisionPromptSurface)window).DisableProtectAction();
                ProtectButton(window).RaiseEvent(KeyArgs(window, Keyboard.PreviewKeyDownEvent, Key.Return));
                return (seen, ((IDecisionPromptSurface)window).CaptureInvocation().ByKeyboard);
            }
            finally { window.Close(); }
        });

        Assert.Empty(presses);
        Assert.False(flagged);
    }

    // ===== the previous application: a THREAD id from the OS, resolved to its process =========================================================

    [Fact]
    public void TheThreadIdFromTheOs_IsResolvedToItsProcess_NeverReadAsAProcessId()
    {
        using var host = new DispatcherAffineTestHost();
        uint thread = GetCurrentThreadId();
        Assert.NotEqual((uint)Environment.ProcessId, thread);           // the two numbers differ, so equality below can only come from the mapping

        var (resolved, unresolvable, afterDeactivation, replaced) = host.Dispatcher.Invoke(() =>
        {
            var window = new DecisionPromptWindow();
            try
            {
                window.NoteApplicationActivation(activated: true, otherAppThreadId: thread);
                uint a = window.RecordedPreviousActiveProcessId;
                window.NoteApplicationActivation(activated: true, otherAppThreadId: 0xFFFFFFF0);
                uint b = window.RecordedPreviousActiveProcessId;
                window.NoteApplicationActivation(activated: true, otherAppThreadId: thread);
                window.NoteApplicationActivation(activated: false, otherAppThreadId: thread);
                uint c = window.RecordedPreviousActiveProcessId;
                window.NoteApplicationActivation(activated: true, otherAppThreadId: thread);
                window.NoteApplicationActivation(activated: true, otherAppThreadId: 0);
                uint d = window.RecordedPreviousActiveProcessId;
                return (a, b, c, d);
            }
            finally { window.Close(); }
        });

        Assert.Equal((uint)Environment.ProcessId, resolved);
        Assert.Equal(0u, unresolvable);                                  // an unresolvable thread is unknown, never an identity
        Assert.Equal(0u, afterDeactivation);                             // the record belongs to one activation only
        Assert.Equal(0u, replaced);                                      // and a later activation replaces it, even with "unknown"
    }

    [Fact]
    public void TheWindowReadsTheOsActivationMessage_ThroughItsOwnHook()
    {
        using var host = new DispatcherAffineTestHost();
        uint thread = GetCurrentThreadId();

        uint recorded = host.Dispatcher.Invoke(() =>
        {
            var window = new DecisionPromptWindow();
            try
            {
                window.Show();
                var hwnd = new WindowInteropHelper(window).Handle;
                SendMessage(hwnd, 0x001C, new IntPtr(1), new IntPtr(thread));   // WM_ACTIVATEAPP(TRUE, <thread of the other application>)
                return window.RecordedPreviousActiveProcessId;
            }
            finally { window.Close(); }
        });

        Assert.Equal((uint)Environment.ProcessId, recorded);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
