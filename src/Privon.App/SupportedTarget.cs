namespace Privon.App;

/// <summary>
/// PRIVON 0.3.0 Gate 1B -- the typed multi-target authorization result. Declared starting at 1
/// (never 0) so <c>default(SupportedTarget)</c> is not a defined member of this enum and can never
/// be mistaken for a supported target -- <see cref="TargetGate.Match"/> represents "unsupported" as
/// absence (<see langword="null"/>), never a sentinel value of this type.
///
/// Deliberately a closed, two-member set for 0.3.0 -- Antigravity Windows is explicitly DEFERRED/
/// UNSUPPORTED (Windows Multi-AI Authorization Contract Freeze, Gate 0B) and has no member here.
/// Adding one is a future, separately-gated product decision, never an incidental side effect of
/// unrelated work.
/// </summary>
internal enum SupportedTarget
{
    ChatGpt = 1,
    Claude = 2,
}
