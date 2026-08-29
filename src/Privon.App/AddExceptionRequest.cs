using Privon.Detection;

namespace Privon.App;

/// <summary>
/// PRIVON v0.2.1 Gate 3C -- the raw, not-yet-validated Add Exception request a real
/// <see cref="ISettingsSurface"/> raises via <see cref="ISettingsSurface.AddExceptionRequested"/>:
/// which of the two approved ordinary types the user selected, and the single raw value they
/// typed. <see cref="RawValue"/> is intentionally NOT a <see cref="CanonicalValue"/> -- Detection's
/// real canonicalizers, run by <see cref="SettingsCoordinator"/>, are the sole owner of
/// canonicalization (CANONICALIZATION contract); no UI-side phone/email normalization exists
/// anywhere in this codebase, and this type must never be treated as if it already carried a
/// validated/canonical value.
///
/// PRIVACY_UI: <see cref="RawValue"/> must never be written into a diagnostic message, an
/// exception's <c>Message</c>, a log, or a window title -- see this project's established
/// PRIVACY_UI discipline (<see cref="DecisionPromptCoordinator.NeutralFailureMessage"/>). It exists
/// only to be handed, once, to the real Detection pipeline.
/// </summary>
internal readonly record struct AddExceptionRequest(PiiType SelectedType, string RawValue)
{
    public override string ToString() => $"{nameof(AddExceptionRequest)} {{ {nameof(SelectedType)} = {SelectedType} }}";
}
