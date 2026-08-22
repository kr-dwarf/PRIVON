using System.Windows.Threading;

namespace Privon.App.Tests;

// Phase 3C STEP40.2 -- a REAL, dedicated STA thread running an actual pumped WPF Dispatcher (via
// Dispatcher.CurrentDispatcher's auto-creation on that thread, then Dispatcher.Run()). No
// System.Windows.Application instance is ever constructed here -- only one may exist per process,
// and constructing an extra one purely to prove thread-affinity would be fragile and could
// interfere with other tests sharing the same test-host process. Used only by
// ProtectAllDispatcherAffinityTests.cs, to prove real WPF thread-affinity enforcement against the
// actual production DecisionPromptCoordinator -- never used by any other, non-Dispatcher-affinity
// test file.
internal sealed class DispatcherAffineTestHost : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Dispatcher> _dispatcherReady = new();
    private bool _disposed;

    public DispatcherAffineTestHost()
    {
        _thread = new Thread(RunMessageLoop) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Dispatcher = _dispatcherReady.Task.GetAwaiter().GetResult();
    }

    /// <summary>The real, dedicated WPF Dispatcher pumped by this host's own STA thread.</summary>
    public Dispatcher Dispatcher { get; }

    /// <summary>The exact managed thread ID of the dedicated STA thread -- lets a test assert an
    /// operation genuinely ran on this thread, independent of <see cref="Dispatcher"/>'s own
    /// <c>VerifyAccess</c> enforcement (defense-in-depth cross-check).</summary>
    public int ThreadId => _thread.ManagedThreadId;

    private void RunMessageLoop()
    {
        _dispatcherReady.SetResult(Dispatcher.CurrentDispatcher);
        Dispatcher.Run();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
