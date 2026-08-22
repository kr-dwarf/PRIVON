namespace Privon.Core;

/// <summary>
/// Declared with <see cref="Unknown"/> first so that default(TrustState) is never
/// mistaken for <see cref="Trusted"/>. A value that cannot be verified (e.g. because its
/// backing store failed to decrypt) must be treated the same as explicitly untrusted.
/// </summary>
public enum TrustState
{
    Unknown = 0,
    Untrusted,
    Trusted,
}

public static class TrustStateExtensions
{
    public static bool IsTrusted(this TrustState state) => state == TrustState.Trusted;
}
