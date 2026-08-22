namespace Privon.App;

/// <summary>
/// Phase 3B STEP6 -- narrow App-owned seam over the Storage &lt;-&gt; Detection trusted/exception
/// bridge (STORAGE_DETECTION_BRIDGE_OWNER, frozen since Phase 2R.5: this bridge is owned by
/// Privon.App, not by Privon.Storage or Privon.Detection). Synchronous -- the underlying
/// <c>PrivonLocalStore.LoadTrustedPublicInfo</c>/<c>LoadExceptions</c> calls are themselves
/// synchronous, local file IO plus a small AES-GCM decrypt, so wrapping this in <c>Task</c> would
/// be an unnecessary abstraction (same reasoning already applied to
/// <see cref="IClipboardPrivacyProcessor"/>).
///
/// This interface exists so App-level orchestration can be tested with a hand-written fake
/// instead of a real <c>PrivonLocalStore</c> where that is useful, without widening
/// <c>Privon.Storage</c>'s or <c>Privon.Detection</c>'s own public surface -- the same pattern
/// already used for <see cref="IClipboardReadTransport"/>/<see cref="IForegroundTargetCapture"/>/
/// <see cref="IClipboardPrivacyProcessor"/>.
/// </summary>
internal interface ITrustExceptionProvider
{
    /// <summary>
    /// Loads and maps whatever trusted/exception entries <c>PrivonLocalStore</c> currently
    /// returns -- freshly, every call. No App-side cache: see
    /// <see cref="TrustExceptionProvider"/>'s own class doc for the CACHE_POLICY rationale.
    /// </summary>
    TrustExceptionSnapshot Load();
}
