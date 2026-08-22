using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3C STEP31/32 -- narrow App-owned seam over <see cref="ComposerTextReader"/>'s guarded
/// read surface, scoped to exactly what <see cref="ClipboardComposerVerifier"/> needs. Mirrors
/// <see cref="IClipboardWriteTransport"/>'s own shape exactly (one read-only operation, no
/// <c>Start</c>/<c>Stop</c>/lifecycle members) -- the underlying <see cref="ComposerTextReader"/>'s
/// lifecycle remains owned by a future composition root, never by this seam's implementation or by
/// <see cref="ClipboardComposerVerifier"/> itself. No target-capture member exists here or anywhere
/// on <see cref="ClipboardComposerVerifier"/> -- the target always comes from the pinned pending
/// record, never a fresh capture.
/// </summary>
internal interface IComposerReadTransport
{
    /// <summary>See <see cref="ComposerTextReader.ReadFocusedComposerTextAsync(ForegroundTargetSnapshot)"/>.</summary>
    Task<ComposerTextReadResult> ReadFocusedComposerTextAsync(ForegroundTargetSnapshot expectedTarget);
}
