using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP2 -- narrow App-owned seam over <see cref="ForegroundTargetInspector"/>, scoped
/// to exactly what <c>ClipboardPrivacyCoordinator</c> needs (one method). Exists purely so
/// App-level orchestration can be tested with a hand-written fake instead of real Win32
/// foreground-window/process inspection -- no policy of any kind lives here or in the production
/// wrapper that implements it.
/// </summary>
internal interface IForegroundTargetCapture
{
    /// <summary>See <see cref="ForegroundTargetInspector.Capture"/>.</summary>
    ForegroundTargetSnapshot Capture();
}
