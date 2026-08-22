using System.Windows.Threading;
using Privon.App;

namespace Privon.App.Tests;

// Phase 3C STEP40.2 -- a REAL System.Windows.Threading.DispatcherObject-derived
// IDecisionPromptSurface double. Every member genuinely enforces WPF thread affinity via
// VerifyAccess() -- the exact same mechanism every real DispatcherObject/Window/FrameworkElement
// (including the production DecisionPromptWindow) uses. This is not an approximation of thread
// affinity; it is the real WPF enforcement, applied to a minimal, non-brittle stand-in so this
// regression does not need to construct a real Window/Button on a pumped Dispatcher thread.
//
// A genuine violation (VerifyAccess() throwing InvalidOperationException because a member was
// entered from a thread other than this object's own owning Dispatcher thread) is caught HERE,
// recorded into ThreadAffinityViolation, and never allowed to propagate into the Dispatcher's own
// message loop -- letting it propagate would either crash the dedicated STA test thread or be
// silently swallowed by WPF's own dispatcher exception handling, either of which would make the
// violation unobservable to the test instead of assertable.
internal sealed class ThreadAffineDecisionPromptSurface : DispatcherObject, IDecisionPromptSurface
{
    public event EventHandler? ProtectAllRequested;
    public event EventHandler? Closed;

    public int ShowCallCount { get; private set; }
    public int CloseCallCount { get; private set; }
    public int DisableProtectActionCallCount { get; private set; }
    public string? LastNeutralFailureMessage { get; private set; }
    public bool IsClosed { get; private set; }

    /// <summary>Set the first time any member below is entered from a thread other than this
    /// object's own owning Dispatcher thread. <see langword="null"/> means every touch so far was
    /// correctly on-thread.</summary>
    public InvalidOperationException? ThreadAffinityViolation { get; private set; }

    /// <summary>Optional test hook invoked at the end of <see cref="Close"/>/<see cref="ShowNeutralFailure"/>
    /// ONLY -- the two TERMINAL Protect-All outcomes -- whether they succeeded or hit a
    /// thread-affinity violation. Deliberately NOT invoked by <see cref="Show"/>/
    /// <see cref="DisableProtectAction"/> (which happen synchronously, before the resolver's async
    /// work even starts) -- a test awaiting this signal is specifically waiting for
    /// <c>RunProtectAllAsync</c>'s eventual post-await continuation, not for the synchronous
    /// click-handling prefix.</summary>
    public Action? OnMutation { get; set; }

    private bool CheckedAccess(Action body, bool signalMutation = false)
    {
        try
        {
            VerifyAccess();
            body();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            ThreadAffinityViolation ??= ex;
            return false;
        }
        finally
        {
            if (signalMutation) OnMutation?.Invoke();
        }
    }

    public void Show() => CheckedAccess(() => ShowCallCount++);

    public void DisableProtectAction() => CheckedAccess(() => DisableProtectActionCallCount++);

    public void ShowNeutralFailure(string message) => CheckedAccess(() => LastNeutralFailureMessage = message, signalMutation: true);

    public void Close() => CheckedAccess(() =>
    {
        if (IsClosed) return;
        IsClosed = true;
        CloseCallCount++;
        Closed?.Invoke(this, EventArgs.Empty);
    }, signalMutation: true);

    /// <summary>Simulates the user's click. The TEST is responsible for invoking this via the
    /// owning Dispatcher (e.g. <c>Dispatcher.Invoke(surface.RaiseProtectAllRequested)</c>) --
    /// exactly like a real WPF <c>Button.Click</c> always fires on its own Dispatcher thread.
    /// <see cref="VerifyAccess"/> here is a genuine safety check on the test's own setup, not a
    /// swallowed one: if a test forgets to invoke this via the Dispatcher, it throws synchronously
    /// back to that test's own (synchronous) call site.</summary>
    public void RaiseProtectAllRequested()
    {
        VerifyAccess();
        ProtectAllRequested?.Invoke(this, EventArgs.Empty);
    }
}
