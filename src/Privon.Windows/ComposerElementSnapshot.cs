namespace Privon.Windows;

/// <summary>
/// Phase 3C STEP29 -- the complete, plain-data result of ONE atomic
/// <see cref="IComposerTextSource.QueryFocusedElement"/> call: <c>AutomationElement.FocusedElement</c>
/// is queried exactly once, and -- entirely within that same native-layer call, against that SAME
/// element reference, never a second <c>FocusedElement</c> query -- its <c>ProcessId</c>/
/// <c>ClassName</c>/<c>ControlType</c> are captured and, if it is an Edit control, its text is
/// also read (<c>ValuePattern</c> first, <c>TextPattern</c> fallback). This bundling is
/// deliberate: it is what lets <see cref="ComposerTextReader"/> avoid ever re-querying
/// <c>FocusedElement</c> a second time (which could observe a DIFFERENT element than the one whose
/// identity was just checked).
///
/// A plain data record -- no <c>AutomationElement</c>/<c>ValuePattern</c>/<c>TextPattern</c>/COM
/// object of any kind crosses this boundary, which is exactly what makes this type safely
/// fake-able in tests without ever needing to construct a real UI Automation object (impossible
/// for a hand-written test double -- <c>AutomationElement</c> has no usable public constructor).
///
/// <see cref="ProcessId"/>/<see cref="ClassName"/>/<see cref="IsEditControlType"/> are meaningful
/// only when <see cref="Status"/> is <see cref="ComposerElementQueryStatus.Resolved"/>.
/// <see cref="Text"/> is populated only when a focused Edit-control element's text was actually
/// read successfully -- <see langword="null"/> whenever <see cref="IsEditControlType"/> is
/// <see langword="false"/> OR neither pattern produced text (an actually-empty, successfully-read
/// composer is represented as <see cref="Text"/> == <c>""</c>, never <see langword="null"/> --
/// <see langword="null"/> means "could not read," not "read as empty").
/// </summary>
internal readonly record struct ComposerElementSnapshot
{
    public required ComposerElementQueryStatus Status { get; init; }
    public uint ProcessId { get; init; }
    public string? ClassName { get; init; }
    public bool IsEditControlType { get; init; }
    public string? Text { get; init; }

    public static ComposerElementSnapshot NoFocusedElement() =>
        new() { Status = ComposerElementQueryStatus.NoFocusedElement };

    public static ComposerElementSnapshot AutomationFailure() =>
        new() { Status = ComposerElementQueryStatus.AutomationFailure };

    public static ComposerElementSnapshot Resolved(uint processId, string? className, bool isEditControlType, string? text) =>
        new()
        {
            Status = ComposerElementQueryStatus.Resolved,
            ProcessId = processId,
            ClassName = className,
            IsEditControlType = isEditControlType,
            Text = text,
        };
}
