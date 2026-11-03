namespace Privon.App;

/// <summary>
/// PRIVON 0.4.0 (policy B) -- what the decision prompt itself can state, synchronously on the UI thread, about the press of "모두 보호" that is being
/// handled right now. It carries no text, no handle and no process name -- only three facts:
/// <list type="bullet">
/// <item><see cref="IsForeground"/>: this prompt is the OS foreground window at this very instant.</item>
/// <item><see cref="ByKeyboard"/>: the click is being raised INSIDE the routing of a fresh Enter (key down) or Space (key up) that was addressed to the
/// "모두 보호" button itself. It is NOT a statement that a person pressed a key: a synthesized key input produces exactly the same fact, and nothing here
/// claims otherwise.</item>
/// <item><see cref="PreviousActiveProcessId"/>: the process that was the active application immediately before this prompt's application became active
/// (resolved from the thread id the OS passes with WM_ACTIVATEAPP, never used as a process id directly). <c>0</c> means unknown, not recorded for the
/// current activation, or unresolvable -- and an unknown value is never treated as any identity.</item>
/// </list>
/// </summary>
internal readonly record struct DecisionPromptInvocation(bool IsForeground, bool ByKeyboard, uint PreviousActiveProcessId);
