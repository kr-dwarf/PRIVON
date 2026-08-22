using System.Windows.Threading;
using Privon.App;

namespace Privon.App.Tests;

// Phase 3C STEP40.2 -- test-only mirror of production WpfDispatcherScheduler, structurally
// identical (asynchronous BeginInvoke, swallows a failed post, never throws back to the caller),
// except it targets an EXPLICIT Dispatcher instance rather than System.Windows.Application.Current
// -- so ProtectAllDispatcherAffinityTests.cs can prove real Dispatcher-affinity enforcement without
// ever constructing a real System.Windows.Application (only one may exist per process).
internal sealed class RealDispatcherScheduler : IDispatcherScheduler
{
    private readonly Dispatcher _dispatcher;

    public RealDispatcherScheduler(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            _dispatcher.BeginInvoke(action);
        }
        catch
        {
            // Mirrors WpfDispatcherScheduler's own EVENT_FAILURE_CONTAINMENT.
        }
    }
}
