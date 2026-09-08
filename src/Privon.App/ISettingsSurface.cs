namespace Privon.App;

/// <summary>
/// PRIVON v0.2.1 Gate 3C -- the minimum Settings UI surface, mirroring
/// <see cref="IDecisionPromptSurface"/>'s own established shape/rationale: a narrow interface so
/// <see cref="SettingsCoordinator"/>'s entire orchestration is deterministically testable with a
/// hand-written fake, never a real WPF <c>Window</c>.
///
/// UNLIKE <see cref="IDecisionPromptSurface"/>/<see cref="DecisionPromptWindow"/>, a Settings
/// surface is a NORMAL, activating window -- it needs real keyboard focus for exception-value entry
/// -- so this interface adds <see cref="Activate"/> (bring an already-open surface to the
/// foreground on a duplicate open request) instead of relying on any no-activate behavior.
///
/// PRESENTATION_ONLY: a real implementation holds only transient display state -- it is never the
/// canonical source of truth (see <see cref="SettingsViewState"/>'s own SOURCE_OF_TRUTH doc). No
/// clipboard/target/retry/generation logic belongs behind this interface at all.
/// </summary>
internal interface ISettingsSurface
{
    /// <summary>Raised exactly once when this surface stops being shown, however that happened --
    /// the user's own system close button, or the coordinator calling <see cref="Close"/> (e.g.
    /// during application shutdown). Never itself triggers a mutation of any kind.</summary>
    event EventHandler? Closed;

    /// <summary>Raised when the user clicks the Phone protection toggle. The <see langword="bool"/>
    /// payload is the NEW desired enabled state the user just requested -- never inferred by the
    /// coordinator from any state this surface might otherwise cache.</summary>
    event EventHandler<bool>? PhoneToggleRequested;

    /// <summary>Raised when the user clicks the Email protection toggle. Same contract as
    /// <see cref="PhoneToggleRequested"/>.</summary>
    event EventHandler<bool>? EmailToggleRequested;

    /// <summary>Raised when the user confirms "Reset protection scope."</summary>
    event EventHandler? ResetProtectionScopeRequested;

    /// <summary>Raised when the user submits the Add Exception form. Carries the RAW, not-yet-
    /// validated request -- see <see cref="AddExceptionRequest"/>'s own doc; canonicalization and
    /// candidate-count/type validation happen entirely in <see cref="SettingsCoordinator"/>, never
    /// in this surface.</summary>
    event EventHandler<AddExceptionRequest>? AddExceptionRequested;

    /// <summary>Raised when the user selects an entry from the exception list and confirms
    /// Delete.</summary>
    event EventHandler<UserExceptionValue>? DeleteExceptionRequested;

    /// <summary>Raised when the user confirms "Reset exceptions."</summary>
    event EventHandler? ResetExceptionsRequested;

    /// <summary>PRIVON 0.3.1 Gate E5G.1C -- raised when the user chooses the explicit Chrome Native
    /// Messaging setup action. Only meaningful while the most recently rendered
    /// <see cref="SettingsViewState.ChromeNativeMessagingReadiness"/> is
    /// <see cref="NativeMessagingRegistrationReadiness.Fresh"/> -- <see cref="SettingsCoordinator"/>,
    /// never this surface, decides what a raise in any other state actually does (the frozen
    /// coordinator's own Provision contract already no-ops outside Fresh).</summary>
    event EventHandler? ChromeNativeMessagingProvisionRequested;

    /// <summary>PRIVON 0.3.1 Gate E5G.1C -- raised when the user chooses the explicit Chrome Native
    /// Messaging repair action. A SEPARATE action from
    /// <see cref="ChromeNativeMessagingProvisionRequested"/> -- only meaningful while readiness is
    /// <see cref="NativeMessagingRegistrationReadiness.OwnedNeedsRepair"/>; never auto-invoked by
    /// opening Settings or by a Provision raise.</summary>
    event EventHandler? ChromeNativeMessagingRepairRequested;

    /// <summary>PRIVON 0.3.1 Gate E5G.1G -- the Edge counterpart of
    /// <see cref="ChromeNativeMessagingProvisionRequested"/>. Same contract, evaluated against
    /// <see cref="SettingsViewState.EdgeNativeMessagingReadiness"/> instead -- Chrome and Edge are
    /// mechanically independent actions.</summary>
    event EventHandler? EdgeNativeMessagingProvisionRequested;

    /// <summary>PRIVON 0.3.1 Gate E5G.1G -- the Edge counterpart of
    /// <see cref="ChromeNativeMessagingRepairRequested"/>.</summary>
    event EventHandler? EdgeNativeMessagingRepairRequested;

    /// <summary>Displays the surface for the first time in its current lifecycle (a fresh
    /// construction, per ONE_CURRENT_SETTINGS_WINDOW -- see <see cref="SettingsCoordinator"/>'s own
    /// doc). Unlike <see cref="IDecisionPromptSurface.Show"/>, this surface activates normally --
    /// there is no no-activate/non-stealing-focus behavior here at all.</summary>
    void Show();

    /// <summary>Brings an ALREADY-open surface to the foreground -- called instead of constructing
    /// a second surface when a new Settings-open request arrives while one is already visible
    /// (ONE_CURRENT_SETTINGS_WINDOW). Never constructs, never re-shows via <see cref="Show"/>.</summary>
    void Activate();

    /// <summary>Idempotent -- closes the surface (if not already closed) and raises
    /// <see cref="Closed"/> at most once.</summary>
    void Close();

    /// <summary>Replaces every displayed value with <paramref name="state"/> -- called on open,
    /// after every mutation attempt (success or failure), and on reopen (SOURCE_OF_TRUTH). Never
    /// merges with or partially updates whatever was displayed before.</summary>
    void RenderState(SettingsViewState state);
}
