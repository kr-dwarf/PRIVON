namespace Privon.App;

/// <summary>
/// Phase 3C STEP40 -- the minimum 0.1 running-presence/Exit surface (Phase 3C STEP39/STEP39.1
/// audits' frozen TRAY_EXIT_BOUNDARY): neutral running presence plus exactly one Exit command --
/// no dashboard, no settings, no status history, no persistent safe/unsafe indicator of any kind.
/// A narrow interface so lifecycle (creation/Exit-request/deterministic disposal) is testable with
/// a hand-written fake, never a real system tray icon, in automated tests -- real tray rendering
/// remains manual release QA (Phase 3C STEP40 instruction).
///
/// Phase 0.2I extends this surface with exactly the one opt-in control the AUTO-START CONTRACT
/// needs -- a single checkable "Windows 시작 시 자동 실행" menu item -- still no dashboard, no
/// settings window, no protection-category toggles (those remain explicitly out of scope for
/// 0.2I, deferred to a future 0.2.1 settings surface).
///
/// PRIVON v0.2.1 Gate 3C adds exactly one more menu item -- "Settings..." -- the tray's own single
/// entry point into the Settings UI (<see cref="SettingsRequested"/>). Still no dashboard, no
/// protection-category toggles ON THE TRAY ITSELF; those now live behind the Settings surface this
/// event opens, never on this menu directly.
/// </summary>
internal interface ITrayIconSurface : IDisposable
{
    /// <summary>Raised when the user chooses the tray's single Exit command.</summary>
    event EventHandler? ExitRequested;

    /// <summary>Raised when the user clicks the auto-start toggle menu item. Carries no state of
    /// its own -- the current actual registration state is always the single source of truth (see
    /// <see cref="WindowsAutoStartCoordinator.IsEnabled"/>), never inferred from this event or from
    /// the menu item's own visual checked state at click time.</summary>
    event EventHandler? AutoStartToggleRequested;

    /// <summary>PRIVON v0.2.1 Gate 3C -- raised when the user chooses the tray's "Settings..."
    /// command. Carries no state of its own; <see cref="SettingsCoordinator"/> is the sole owner of
    /// whether this opens a fresh Settings surface or activates an already-open one
    /// (ONE_CURRENT_SETTINGS_WINDOW).</summary>
    event EventHandler? SettingsRequested;

    /// <summary>PRIVON 0.3.2 Gate 032-C2 -- raised when the user clicks the Chrome reconnect
    /// notification <see cref="ShowChromeReconnectNotification"/> displayed. Carries no state of its
    /// own; the ONLY authorized reaction is opening/focusing the existing Settings surface -- never
    /// Repair, never any registry/manifest mutation. <see cref="PrivonAppUiBridge"/> is the sole
    /// owner of what this actually does.</summary>
    event EventHandler? ChromeReconnectNotificationClicked;

    /// <summary>Makes the tray icon visible. A neutral tooltip only (e.g. "PRIVON — 실행 중") --
    /// never "보호 중"/"보호 완료"/"검증됨" or any other claim this codebase's frozen
    /// <c>ProtectionState</c> boundary does not support yet.</summary>
    void Show();

    /// <summary>Reflects the auto-start toggle menu item's checked state. The caller is
    /// responsible for only ever passing the ACTUAL current registration state (re-queried after
    /// every enable/disable attempt, success or failure) -- this method itself performs no
    /// verification of its own, it only renders whatever state it is given.</summary>
    void SetAutoStartChecked(bool isChecked);

    /// <summary>PRIVON 0.3.2 Gate 032-C2 -- displays exactly one informational, non-technical
    /// notification telling the user their Chrome connection needs updating. Never itself mutates
    /// anything; the caller decides whether/when to show it (at most once per process, per the
    /// COMMANDER_CONTRACT this gate adds) -- this method has no de-duplication of its own. The OS
    /// may suppress the visible balloon (e.g. Focus Assist) -- that is acceptable, since the
    /// existing tray Settings menu item remains a working fallback regardless.</summary>
    void ShowChromeReconnectNotification();
}
