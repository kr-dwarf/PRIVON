namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F4.1 (frozen contract) / Gate 031F4.3 (this type) -- the narrow, read-only
/// seam over the App-owned foreground-occupancy epoch, scoped to exactly the one question a
/// freshness decision needs to ask. Mirrors <see cref="IForegroundTargetCapture"/>'s own
/// narrow-seam-over-a-richer-concrete-type pattern, so a consumer can be exercised without
/// constructing a real <see cref="ForegroundEpochTracker"/> or driving a real foreground-change
/// signal.
///
/// EPOCH_DOMAIN (frozen): a valid epoch is <c>1 .. long.MaxValue</c> -- strictly monotonic, never
/// reused, never negative, never wrapping. <c>0</c> is NOT an epoch: it is the single fail-closed
/// sentinel meaning UNAVAILABLE, covering "not yet established", "exhausted", and "disposed"
/// identically, so a consumer never needs to distinguish them and can never authorize on any of
/// them.
///
/// SYNCHRONOUS_ONLY: deliberately a plain property, never <c>async</c>/<c>Task</c>-returning. It is
/// read from the clipboard owner thread while that thread holds the global clipboard critical
/// section, so it must answer immediately from already-held state.
///
/// Carries no product, browser, or origin policy of any kind, and no Web concept -- an epoch is a
/// purely mechanical foreground fact.
/// </summary>
internal interface IForegroundEpochSource
{
    long CurrentEpoch { get; }
}
