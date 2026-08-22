namespace Privon.App;

/// <summary>
/// Phase 3C STEP40 -- the narrow WPF_DISPATCHER_BOUNDARY seam (Phase 3C STEP39.1 audit, frozen):
/// posts <paramref name="action"/> to run later, asynchronously, on whatever UI-affine thread this
/// implementation owns -- never blocking the caller. <see cref="DecisionPromptCoordinator"/> is
/// given this instead of touching <see cref="System.Windows.Threading.Dispatcher"/> directly so it
/// stays testable with a deterministic fake scheduler (no arbitrary sleeps needed to prove
/// staleness-after-dispatch races -- Phase 3C STEP40 instruction's DISPATCHER_STALENESS_TESTS).
/// </summary>
internal interface IDispatcherScheduler
{
    /// <summary>Schedules <paramref name="action"/> to run later -- never synchronously, never
    /// blocking. A production implementation that cannot currently schedule anything (e.g. no
    /// <see cref="System.Windows.Application"/> exists, or its <c>Dispatcher</c> is already
    /// shutting down) silently drops the action rather than throwing -- see
    /// <see cref="WpfDispatcherScheduler"/>'s own EVENT_FAILURE_CONTAINMENT doc.</summary>
    void Post(Action action);
}
