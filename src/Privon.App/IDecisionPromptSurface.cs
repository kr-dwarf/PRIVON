namespace Privon.App;

/// <summary>
/// Phase 3C STEP40 -- the minimum 0.1 NeedsDecision UI surface (Phase 3C STEP39/STEP39.1 audits'
/// frozen MINIMUM_DECISION_ACTION/PROTECT_ALL_SEMANTICS). Exposes exactly one user action
/// (<see cref="ProtectAllRequested"/>, "모두 보호") -- no <c>BypassOnce</c>/"그대로 보내기"/"원문
/// 사용"/exception-or-trust-registration control of any kind exists anywhere behind this
/// interface. <see cref="DecisionPromptCoordinator"/> depends only on this narrow interface (never
/// on <see cref="DecisionPromptWindow"/> directly), so it can be driven deterministically by a
/// hand-written fake in tests without ever constructing a real WPF <c>Window</c>.
/// </summary>
internal interface IDecisionPromptSurface
{
    /// <summary>Raised when the user clicks the single "모두 보호" action.</summary>
    event EventHandler? ProtectAllRequested;

    /// <summary>Raised exactly once when this surface stops being shown, however that happened --
    /// a completed Protect-All sequence closing it, a caller explicitly calling
    /// <see cref="Close"/> (e.g. because a newer scope superseded it), or the user closing it
    /// themselves (e.g. the window's own system close button). <see cref="DecisionPromptCoordinator"/>
    /// never distinguishes these causes -- see this type's own USER_CLOSE doc precedent on
    /// <see cref="DecisionPromptCoordinator"/>: closing never resolves anything and never claims
    /// success.</summary>
    event EventHandler? Closed;

    /// <summary>Displays the surface. Content is deliberately generic -- no raw clipboard text, no
    /// <c>CanonicalValue</c>, no PiiType, no item count is required.</summary>
    void Show();

    /// <summary>Disables the "모두 보호" action immediately -- called synchronously, before any
    /// await, the instant <see cref="ProtectAllRequested"/> is handled (Phase 3C STEP40
    /// instruction's double-action prevention).</summary>
    void DisableProtectAction();

    /// <summary>Replaces the displayed content with a neutral, non-content-bearing
    /// error/instruction message (e.g. "처리할 수 없습니다. 다시 복사해 주세요.") -- never a
    /// protection-success claim, never "Verified"/"보호 완료"/"검증 완료".</summary>
    void ShowNeutralFailure(string message);

    /// <summary>Idempotent -- closes the surface (if not already closed) and raises
    /// <see cref="Closed"/> at most once.</summary>
    void Close();
}
