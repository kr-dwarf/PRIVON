namespace Privon.Windows;

/// <summary>
/// Phase 3C STEP29 -- test seam over the UI Automation primitives
/// <see cref="ComposerTextReader"/> needs, matching this codebase's existing native-seam pattern
/// (<see cref="IClipboardMonitorNative"/>, <see cref="IClipboardTextNative"/>,
/// <see cref="IForegroundTargetSource"/>): an interface with no product policy of its own, so
/// <see cref="ComposerTextReader"/>'s identity/ordering/failure logic can be exercised
/// deterministically without any real ChatGPT window, UI Automation tree, or COM object of any
/// kind. The only production implementation is <see cref="Win32ComposerTextSource"/>.
///
/// This seam is deliberately narrower than "give me the composer's text": it exposes exactly the
/// one atomic native operation <see cref="ComposerTextReader"/> needs
/// (<see cref="QueryFocusedElement"/>) and returns only plain data
/// (<see cref="ComposerElementSnapshot"/>) -- never an <c>AutomationElement</c>/<c>ValuePattern</c>/
/// <c>TextPattern</c> or any other UI Automation/COM object. "Is this the composer" (comparing the
/// snapshot's <see cref="ComposerElementSnapshot.ProcessId"/>/<see cref="ComposerElementSnapshot.ClassName"/>/
/// <see cref="ComposerElementSnapshot.IsEditControlType"/> against an expected target) and the
/// Win32-level foreground CHECK1/CHECK2 guard both live entirely in <see cref="ComposerTextReader"/>
/// itself, never here.
/// </summary>
internal interface IComposerTextSource
{
    /// <summary>
    /// Queries <c>AutomationElement.FocusedElement</c> exactly once and, atomically, within that
    /// same call, captures its identity facts and -- if it is an Edit control -- attempts to read
    /// its text (<c>ValuePattern</c> first, <c>TextPattern</c> fallback). Never re-queries
    /// <c>FocusedElement</c> a second time. Never throws for an ordinary/expected UI Automation
    /// condition -- see <see cref="ComposerElementQueryStatus.AutomationFailure"/>.
    /// </summary>
    ComposerElementSnapshot QueryFocusedElement();
}
