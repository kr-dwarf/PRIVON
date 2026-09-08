using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP14 -- narrow App-owned seam over <see cref="ClipboardChangeMonitor"/>'s guarded
/// write surface, scoped to exactly what <see cref="ClipboardPrivacyCoordinator"/> needs.
/// Deliberately a SEPARATE interface from <see cref="IClipboardReadTransport"/> rather than an
/// added member on it -- that interface is read-only by design and structurally tested as such
/// (<c>IClipboardReadTransport_HasNoWriteMethod</c>); this STEP does not touch it.
///
/// This interface exists so App-level orchestration can be tested with a hand-written fake
/// instead of a real Win32 clipboard write, without widening <c>Privon.Windows</c>'s own public
/// surface (Phase 3A.5 STEP4 deliberately narrowed it to guarded-only) or reaching into its
/// internal native seams via <c>InternalsVisibleTo</c> -- the same reasoning already used for
/// <see cref="IClipboardReadTransport"/>/<see cref="IForegroundTargetCapture"/>.
/// </summary>
internal interface IClipboardWriteTransport
{
    /// <summary>See <see cref="ClipboardChangeMonitor.WriteTextIfSequenceMatchesAsync(ForegroundTargetSnapshot, uint, string, IClipboardAuthorizationFreshness?)"/>.
    /// PRIVON 0.3.1 Gate 031F5C -- <paramref name="authorizationFreshness"/> is an optional
    /// trailing pass-through, matching <see cref="IClipboardReadTransport.ReadTextSnapshotAsync"/>'s
    /// own identical convention: <see langword="null"/> preserves exact pre-0.3.1 Windows behavior.</summary>
    Task<ClipboardWriteResult> WriteTextIfSequenceMatchesAsync(
        ForegroundTargetSnapshot expectedTarget, uint expectedSequence, string replacementText,
        IClipboardAuthorizationFreshness? authorizationFreshness = null);
}
