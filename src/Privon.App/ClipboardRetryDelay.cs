namespace Privon.App;

/// <summary>
/// BUG-002 Gate 2B -- the only production implementation of <see cref="IClipboardRetryDelay"/>: a
/// single direct delegation to <see cref="Task.Delay(TimeSpan)"/>, with no state of its own, no
/// timer object, no cancellation, and no policy (the retry budget and interval belong to the
/// caller, never here). Mirrors <see cref="ClipboardReadTransport"/>'s/<see cref="ClipboardForegroundTrigger"/>'s
/// own thin production-adapter shape exactly.
///
/// The production default <see cref="ClipboardPrivacyCoordinator"/> constructs when no test-only
/// <see cref="IClipboardRetryDelay"/> override is supplied -- see
/// <see cref="IClipboardRetryDelay"/>'s own GATE_2B_BOUNDARY doc for the now-live autonomous-retry
/// loop that calls it.
/// </summary>
internal sealed class ClipboardRetryDelay : IClipboardRetryDelay
{
    public Task DelayAsync(TimeSpan duration) => Task.Delay(duration);
}
