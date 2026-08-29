using Privon.Storage;

namespace Privon.App;

/// <summary>
/// PRIVON v0.2.1 Gate 3A -- narrow App-owned seam over the Storage &lt;-&gt; category-policy bridge,
/// mirroring <see cref="ITrustExceptionProvider"/>'s own established shape and rationale exactly
/// (STORAGE_DETECTION_BRIDGE_OWNER precedent, extended here to Storage &lt;-&gt; App policy rather
/// than Storage &lt;-&gt; Detection). Synchronous -- <see cref="PrivonLocalStore.LoadSettings"/> is
/// itself synchronous, local file IO plus a small AES-GCM decrypt, so wrapping this in
/// <c>Task</c> would be an unnecessary abstraction (same reasoning already applied to
/// <see cref="IClipboardPrivacyProcessor"/>/<see cref="ITrustExceptionProvider"/>).
///
/// Exists so App-level orchestration can be tested with a hand-written fake instead of a real
/// <c>PrivonLocalStore</c>, without widening <c>Privon.Storage</c>'s own public surface -- the
/// same pattern already used for <see cref="IClipboardReadTransport"/>/
/// <see cref="IForegroundTargetCapture"/>/<see cref="IClipboardPrivacyProcessor"/>/
/// <see cref="ITrustExceptionProvider"/>.
/// </summary>
internal interface IProtectionCategorySettingsProvider
{
    /// <summary>
    /// Loads the current per-category protection preference -- freshly, every call. No App-side
    /// cache, matching <see cref="ITrustExceptionProvider.Load"/>'s own CACHE_POLICY: a category
    /// toggled in SettingsUI must take effect on the very next clipboard attempt, never require a
    /// restart. Never returns a partially-populated or null value -- always a complete
    /// <see cref="ProtectionCategorySettings"/>, resolved to
    /// <see cref="ProtectionCategorySettings.AllOn"/> whenever Storage itself would have (missing
    /// file, corruption, pre-migration data -- see <see cref="PrivonLocalStore.LoadSettings"/>'s
    /// own BACKWARD_COMPATIBILITY doc).
    /// </summary>
    ProtectionCategorySettings Load();
}
