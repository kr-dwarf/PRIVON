namespace Privon.App;

/// <summary>
/// Phase 3C STEP40 -- the minimum 0.1 running-presence/Exit surface (Phase 3C STEP39/STEP39.1
/// audits' frozen TRAY_EXIT_BOUNDARY): neutral running presence plus exactly one Exit command --
/// no dashboard, no settings, no status history, no persistent safe/unsafe indicator of any kind.
/// A narrow interface so lifecycle (creation/Exit-request/deterministic disposal) is testable with
/// a hand-written fake, never a real system tray icon, in automated tests -- real tray rendering
/// remains manual release QA (Phase 3C STEP40 instruction).
/// </summary>
internal interface ITrayIconSurface : IDisposable
{
    /// <summary>Raised when the user chooses the tray's single Exit command.</summary>
    event EventHandler? ExitRequested;

    /// <summary>Makes the tray icon visible. A neutral tooltip only (e.g. "PRIVON — 실행 중") --
    /// never "보호 중"/"보호 완료"/"검증됨" or any other claim this codebase's frozen
    /// <c>ProtectionState</c> boundary does not support yet.</summary>
    void Show();
}
