using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Privon.Windows;

/// <summary>
/// Phase 3C STEP29 -- the only production implementation of <see cref="IComposerTextSource"/>.
/// Every managed <c>System.Windows.Automation</c> call this type makes is expected to run on the
/// dedicated MTA worker thread <see cref="ComposerTextReader"/> owns (this type itself has no
/// thread affinity/lifecycle of its own -- it is a stateless, on-demand query only).
///
/// SPIKE_CODE_REUSE_BOUNDARY (Phase 3C STEP28, reconfirmed): reimplements -- narrowly, not a
/// direct copy -- exactly the newer-generation Phase0 evidence
/// (<c>IsComposerFocusedV2</c>/<c>CombinedStressTest</c>'s <c>ReadComposerTextSafe</c>): query
/// <c>AutomationElement.FocusedElement</c> directly (no tree walk, no positional heuristic), read
/// <c>ValuePattern</c> then fall back to <c>TextPattern</c>. Never calls <c>ValuePattern.SetValue</c>,
/// <c>SendInput</c>, <c>RegisterHotKey</c>, or anything overlay/Send-button related -- none of
/// that exists anywhere in this file.
///
/// EXPECTED_UIA_EXCEPTIONS (Phase 3C STEP28's FAILURE_MODEL, implemented here): only
/// <see cref="ElementNotAvailableException"/> (the one UI Automation exception type Phase0's own
/// evidence actually catches -- e.g. an element becoming stale mid-walk) and
/// <see cref="COMException"/> (the underlying UI Automation provider infrastructure is COM-based,
/// so a provider-side failure surfaces this way) are treated as ordinary, expected environmental
/// failures here, mapped to <see cref="ComposerElementQueryStatus.AutomationFailure"/> -- never
/// thrown. Any other, truly unanticipated exception type is allowed to propagate (this type does
/// not swallow it) -- <see cref="ComposerTextReader"/>'s own outer per-request catch is the
/// load-bearing safety net that keeps its worker thread alive regardless (see that type's own
/// WORKER_SURVIVAL doc), not a second layer of exception-type guessing here.
/// </summary>
internal sealed class Win32ComposerTextSource : IComposerTextSource
{
    public ComposerElementSnapshot QueryFocusedElement()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null)
                return ComposerElementSnapshot.NoFocusedElement();

            uint processId = (uint)focused.Current.ProcessId;
            string? className = focused.Current.ClassName;
            bool isEdit = focused.Current.ControlType == ControlType.Edit;

            string? text = isEdit ? TryReadText(focused) : null;

            return ComposerElementSnapshot.Resolved(processId, className, isEdit, text);
        }
        catch (ElementNotAvailableException)
        {
            return ComposerElementSnapshot.AutomationFailure();
        }
        catch (COMException)
        {
            return ComposerElementSnapshot.AutomationFailure();
        }
    }

    // TEXT_READ_ORDER (Phase 3C STEP28, frozen): ValuePattern.Current.Value first,
    // TextPattern.DocumentRange.GetText(-1) fallback -- exactly the order Phase0's own
    // ReadText/ReadComposerTextSafe helpers both used. Returns null (never throws) if neither
    // pattern is supported -- this method is only ever reached from inside the try block above,
    // so an ElementNotAvailableException/COMException raised here is still caught by that same
    // handler.
    private static string? TryReadText(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObj))
            return ((ValuePattern)valuePatternObj).Current.Value ?? "";

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObj))
            return ((TextPattern)textPatternObj).DocumentRange.GetText(-1) ?? "";

        return null;
    }
}
