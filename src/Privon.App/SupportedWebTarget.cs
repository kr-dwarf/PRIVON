namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031C (frozen) / Gate 031E1 (this type) -- the typed Web authorization result,
/// returned by <see cref="WebTargetGate.Match"/>. Deliberately a SEPARATE enum from
/// <see cref="SupportedTarget"/>, never an addition to it -- Gate 031C section 13/20
/// (WINDOWS_FROZEN_BOUNDARY) requires WebTargetGate to be ADDITIVE and DISJOINT from TargetGate,
/// and <see cref="SupportedTarget"/> is itself already a closed, regression-locked two-member set
/// (<c>Gate0D_TargetGateAndClaudeRuleTests.Gate0D_T1</c> asserts it contains EXACTLY
/// {ChatGpt, Claude} via an ordered name comparison) -- adding a Web member to it would fail that
/// existing regression lock outright, not merely blur a boundary. A separate enum keeps the
/// Windows and Web authorization results structurally impossible to confuse with one another at
/// the type level, which a shared enum could not guarantee.
///
/// Declared starting at 1 (never 0), mirroring <see cref="SupportedTarget"/>'s own discipline: so
/// <c>default(SupportedWebTarget)</c> is not a defined member and can never be mistaken for a
/// supported Web target -- <see cref="WebTargetGate.Match"/> represents "no match" as absence
/// (<see langword="null"/>), never a sentinel value of this type.
/// </summary>
internal enum SupportedWebTarget
{
    ChatGptWeb = 1,
    ClaudeWeb = 2,
    GeminiWeb = 3,
    GrokWeb = 4,
    DeepSeekWeb = 5,
}
