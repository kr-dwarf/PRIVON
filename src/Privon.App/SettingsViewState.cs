namespace Privon.App;

/// <summary>
/// PRIVON v0.2.1 Gate 3C -- the complete, always-authoritative snapshot <see cref="SettingsCoordinator"/>
/// hands to <see cref="ISettingsSurface.RenderState"/> every time the surface must reflect the
/// current persisted truth: on open, after every mutation attempt (success or failure), and on
/// reopen. SOURCE_OF_TRUTH (frozen for this Gate): the surface never derives or retains its own
/// competing copy of this state between renders -- it only ever displays whatever
/// <see cref="RenderState"/> was most recently called with.
///
/// <see cref="StatusMessage"/> is always a short, generic, non-content-bearing string (or
/// <see langword="null"/> when there is nothing to report) -- it NEVER includes the actual entered
/// PII value, matching the identical PRIVACY_UI discipline already established by
/// <see cref="DecisionPromptCoordinator.NeutralFailureMessage"/>.
/// </summary>
/// <summary>
/// What the United States control shows. <see cref="Enabled"/> is the EFFECTIVE state the detection scope will follow: only a verified, stored Off is
/// unchecked; a verified On, a missing record and every unverifiable record are checked (the pack stays active). <see cref="Basis"/> says which one it is,
/// so the surface can say when the shown value is only the safe default. <see cref="Editable"/> is false when a change could not be stored (the stored record
/// is of a newer version, or no usable key exists).
/// </summary>
internal sealed record UnitedStatesChoiceView(
    bool Enabled, Privon.Storage.CountryChoiceState Basis, bool Editable, Privon.UsProductIntegration.UsOptInGate Gate = Privon.UsProductIntegration.UsOptInGate.NotGated);

internal sealed record SettingsViewState(
    bool PhoneEnabled,
    bool EmailEnabled,
    IReadOnlyList<UserExceptionValue> Exceptions,
    bool MasterKeyUnavailable,
    string? StatusMessage,
    NativeMessagingRegistrationReadiness ChromeNativeMessagingReadiness = NativeMessagingRegistrationReadiness.Failed,
    NativeMessagingRegistrationReadiness EdgeNativeMessagingReadiness = NativeMessagingRegistrationReadiness.Failed,
    UnitedStatesChoiceView? UnitedStatesChoice = null);
