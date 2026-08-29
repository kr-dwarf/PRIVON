namespace Privon.App;

/// <summary>
/// BUG-002 Gate 2B -- the minimal, App-owned seam over an asynchronous, non-blocking pause,
/// used by the bounded autonomous-retry loop live in
/// <see cref="ClipboardPrivacyCoordinator"/> (one method). Exists so that loop's
/// timing can be driven deterministically from a test -- a hand-written fake that completes,
/// or blocks, on the test's own command -- instead of a real wall-clock
/// <see cref="Task.Delay(TimeSpan)"/> that would make every retry regression timing-dependent and
/// flaky under unrelated CI load.
///
/// GATE_2B_BOUNDARY (this STEP, as originally scoped): at the time this seam was introduced,
/// nothing in production called <see cref="DelayAsync"/> yet -- <see cref="ClipboardPrivacyCoordinator"/>
/// merely accepted and held an instance so the RED suite written in that same STEP could inject one
/// and assert it was never invoked. BUG-002's bounded autonomous-retry loop has since been
/// implemented (see <see cref="ClipboardPrivacyCoordinator.ProcessWorkItemAsync"/>'s own retry loop
/// and <see cref="ClipboardAutonomousRetryClassifier"/>), and now calls <see cref="DelayAsync"/>
/// before every retry attempt.
///
/// Deliberately NOT a general scheduler/timer subsystem, NOT <c>TimeProvider</c> (whose test double
/// lives in a separate NuGet package, against this codebase's established dependency-free /
/// no-mocking-framework discipline), and NOT a <see cref="System.Threading.CancellationToken"/>-bearing
/// API -- consistent with every other async seam here (see <see cref="IClipboardOperationGate"/>'s
/// own ASYNC_ONLY / SHUTDOWN_BEHAVIOR doc for why no cancellation token is introduced).
/// </summary>
internal interface IClipboardRetryDelay
{
    /// <summary>Completes once <paramref name="duration"/> has elapsed. Never blocks the calling
    /// thread.</summary>
    Task DelayAsync(TimeSpan duration);
}
