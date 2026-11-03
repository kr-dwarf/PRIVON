using Privon.Windows;

namespace Privon.App;

/// <summary>Why a press made while PRIVON's own prompt was the foreground may stand in for the supported target the decision was shown for.</summary>
internal enum DecisionPromptAuthorityBasis
{
    /// <summary>The application that was active immediately before the prompt's application became active IS the original supported target (same process).</summary>
    PreviousAppWasOriginalTarget,

    /// <summary>
    /// A manual decision, by Controller policy (PRIVON 0.4.0 B): the click was raised inside a fresh Enter / Space routed to the button itself. This is NOT a
    /// restoration of the original target and NOT proof that a person pressed the key (synthesized input is indistinguishable). Which application the
    /// keyboard user came from is not observable (the Alt+Tab switcher is the previous application), so no target is claimed for it.
    /// </summary>
    ManualKeyboardDecision,
}

/// <summary>
/// PRIVON 0.4.0 -- the narrow permission a press made while the decision prompt ITSELF was the foreground window may carry. It exists only when the
/// prompt was the foreground at the press, the foreground recorded when the decision was shown was a SUPPORTED Windows target, and the press has one of the
/// two <see cref="DecisionPromptAuthorityBasis"/> values; it carries that supported target and the basis, nothing else: no text, no canonical value, no
/// window handle. It never widens what is supported (see <see cref="ClipboardDecisionActionResolver"/>, PROMPT_FOREGROUND_CARVE_OUT), and it is built
/// fresh for each press -- never stored, never reused.
/// </summary>
internal sealed record DecisionPromptForegroundContext(ForegroundTargetSnapshot OriginalTarget, DecisionPromptAuthorityBasis Basis);
