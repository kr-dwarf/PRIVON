namespace Privon.App;

/// <summary>
/// Phase 3C STEP40 -- the only production implementation of <see cref="IDispatcherScheduler"/>.
/// Posts via <see cref="System.Windows.Application.Current"/>'s own
/// <see cref="System.Windows.Threading.Dispatcher.BeginInvoke(Action)"/> -- asynchronous,
/// non-blocking (Phase 3C STEP39.1 audit's frozen WPF_DISPATCHER_BOUNDARY: never
/// <c>Dispatcher.Invoke</c>, which would block whatever thread called <see cref="Post"/> --
/// specifically the coordinator's own ThreadPool worker via
/// <see cref="ClipboardDecisionSessionPublisher.ScopePublished"/> -- waiting on the UI thread).
///
/// EVENT_FAILURE_CONTAINMENT (Phase 3C STEP40 instruction, frozen): if
/// <see cref="System.Windows.Application.Current"/> is <see langword="null"/> (no WPF application
/// running -- e.g. under an automated test, or a narrow startup window) or the dispatcher is
/// already shutting down/shut down (throws <see cref="System.Windows.Threading.DispatcherException"/>-family
/// exceptions from <c>BeginInvoke</c> itself in that state), the failure is swallowed here and
/// nothing is scheduled -- this type's entire purpose is UI marshaling, never a channel any core
/// protection logic depends on for correctness. No content of any kind ever passes through this
/// type, and no logging of any kind is performed here.
/// </summary>
internal sealed class WpfDispatcherScheduler : IDispatcherScheduler
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            dispatcher?.BeginInvoke(action);
        }
        catch
        {
            // EVENT_FAILURE_CONTAINMENT -- see this type's own class doc.
        }
    }
}
