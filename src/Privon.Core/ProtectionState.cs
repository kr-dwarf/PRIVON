namespace Privon.Core;

/// <summary>
/// User/UX-facing internal protection state. Declared with <see cref="Initializing"/>
/// first so that default(ProtectionState) is never mistaken for a protected state.
/// </summary>
public enum ProtectionState
{
    Initializing = 0,
    Ready,
    Scanning,
    NeedsDecision,
    Protecting,
    Verified,
    Blocked,
    Limited,
    Paused,
    Error,
}

public static class ProtectionStateExtensions
{
    /// <summary>
    /// True only for the one state that means "protection is confirmed for the current
    /// revision". Every other state -- including the CLR default -- must not be treated
    /// as protected. Ambiguous or corrupted states never recover into a claim of safety.
    /// </summary>
    public static bool ClaimsProtected(this ProtectionState state) => state == ProtectionState.Verified;
}
