namespace Privon.App;

/// <summary>
/// Phase 0.2D (STEP61) -- the narrow, coordinator-facing seam for a foreground-focus-change signal,
/// scoped to exactly what <see cref="ClipboardPrivacyCoordinator"/> needs (a payload-free arrival
/// event plus start/stop) -- the same narrow-seam-over-a-richer-concrete-type pattern already used
/// for <see cref="IClipboardReadTransport"/>/<see cref="IForegroundTargetCapture"/>/
/// <see cref="IClipboardWriteTransport"/>. This STEP defines the seam and lets the coordinator
/// depend on it (as an OPTIONAL trailing dependency -- see
/// <see cref="ClipboardPrivacyCoordinator"/>'s own constructor doc); wiring a real production
/// implementation over <c>Privon.Windows.ForegroundChangeMonitor</c> is deliberately deferred to
/// Phase 0.2E (this STEP's own tests use only a hand-written fake -- see
/// <c>FakeClipboardForegroundTrigger</c>).
///
/// <see cref="Changed"/> is deliberately payload-free -- a foreground-focus change carries no
/// clipboard generation, no process identity, no window handle, no title; the coordinator's own
/// pre-pipeline intake step performs its own fresh <see cref="IForegroundTargetCapture.Capture"/>
/// and <see cref="IClipboardGenerationSnapshot.CurrentGeneration"/> read once this event fires,
/// never trusting anything carried on the event itself.
/// </summary>
internal interface IClipboardForegroundTrigger
{
    /// <summary>Raised synchronously on the underlying owner thread every time the OS foreground
    /// window changes -- see <c>Privon.Windows.ForegroundChangeMonitor</c>'s own
    /// CALLBACK_THREAD_CONTRACT-equivalent doc for the full contract a real production
    /// implementation carries. No dedup/debounce/coalescing happens before this event fires -- that
    /// responsibility belongs entirely to the coordinator's own capacity-1 <c>DropOldest</c>
    /// mailbox, exactly like it already does for clipboard-change notifications.</summary>
    event EventHandler? Changed;

    void Start();
    void Stop();
}
